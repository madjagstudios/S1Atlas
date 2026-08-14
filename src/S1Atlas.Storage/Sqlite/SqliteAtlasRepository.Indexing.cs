using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    private static readonly TimeSpan StaleIndexRunAfter = TimeSpan.FromHours(1);

    public async Task CreateCodeSnapshotAsync(
        CodeSnapshotRecord snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO code_snapshots
                (snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc)
            VALUES ($id, $codebase, $channel, $environment, $identity, $created);
            """;
        AddSnapshotParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, codebase, channel, source_identity, created_at_utc, environment_snapshot_id
            FROM code_snapshots WHERE snapshot_id = $id;
            """;
        command.Parameters.AddWithValue("$id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    public async Task StartIndexRunAsync(
        IndexRunRecord run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status != IndexRunStatus.Running)
            throw new ArgumentException("An index run must start in Running status.", nameof(run));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc)
            VALUES ($id, $snapshot, 'Running', $started)
            ON CONFLICT(index_id) DO UPDATE SET
                snapshot_id = excluded.snapshot_id,
                status = 'Running',
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = NULL,
                failure_message = NULL
            WHERE index_runs.status = 'Failed'
               OR (index_runs.status = 'Running' AND index_runs.started_at_utc < $stale);
            """;
        command.Parameters.AddWithValue("$id", run.IndexId);
        command.Parameters.AddWithValue("$snapshot", run.SnapshotId);
        command.Parameters.AddWithValue("$started", run.StartedAtUtc);
        command.Parameters.AddWithValue("$stale", DateTimeOffset.UtcNow.Subtract(StaleIndexRunAfter).ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Index run '{run.IndexId}' is already completed or actively running.");
    }

    public async Task CompleteIndexRunAsync(
        string indexId,
        IndexWriteSet writeSet,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentNullException.ThrowIfNull(writeSet);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var snapshotId = await GetRunningSnapshotIdAsync(connection, transaction, indexId, cancellationToken)
                ?? throw new InvalidOperationException($"Index run '{indexId}' is not running.");

            foreach (var sourceFile in writeSet.SourceFiles) await InsertSourceFileAsync(connection, transaction, sourceFile, cancellationToken);
            foreach (var symbol in writeSet.Symbols) await InsertSymbolAsync(connection, transaction, symbol, cancellationToken);
            foreach (var location in writeSet.SourceLocations) await InsertSourceLocationAsync(connection, transaction, location, cancellationToken);
            foreach (var fingerprint in writeSet.Fingerprints) await InsertFingerprintAsync(connection, transaction, fingerprint, cancellationToken);
            foreach (var relationship in writeSet.Relationships) await InsertRelationshipAsync(connection, transaction, relationship, cancellationToken);

            if (writeSet.Symbols.Any(symbol => !string.Equals(symbol.SnapshotId, snapshotId, StringComparison.Ordinal)) ||
                writeSet.SourceFiles.Any(file => !string.Equals(file.SnapshotId, snapshotId, StringComparison.Ordinal)) ||
                writeSet.Relationships.Any(edge => !string.Equals(edge.SnapshotId, snapshotId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Index write-set rows must belong to the running snapshot.");

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE index_runs
                SET status = 'Completed', completed_at_utc = $completed, failure_message = NULL
                WHERE index_id = $id AND status = 'Running';
                """;
            update.Parameters.AddWithValue("$completed", completedAtUtc);
            update.Parameters.AddWithValue("$id", indexId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Index run '{indexId}' could not be completed.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task FailIndexRunAsync(
        string indexId,
        string failureMessage,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE index_runs
            SET status = 'Failed', completed_at_utc = $completed, failure_message = $message
            WHERE index_id = $id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$completed", completedAtUtc);
        command.Parameters.AddWithValue("$message", failureMessage);
        command.Parameters.AddWithValue("$id", indexId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Index run '{indexId}' could not be failed.");
    }

    public async Task<IndexRunRecord?> GetLatestCompletedIndexAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string? environmentSnapshotId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run.index_id, run.snapshot_id, run.status, run.started_at_utc,
                   run.completed_at_utc, run.failure_message
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot ON snapshot.snapshot_id = run.snapshot_id
            WHERE run.status = 'Completed'
              AND snapshot.codebase = $codebase
              AND snapshot.channel = $channel
              AND ($environment IS NULL OR snapshot.environment_snapshot_id = $environment)
            ORDER BY run.completed_at_utc DESC, run.index_id COLLATE BINARY DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$codebase", codebase.ToString());
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$environment", (object?)environmentSnapshotId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT index_id, snapshot_id, status, started_at_utc, completed_at_utc, failure_message
            FROM index_runs
            WHERE index_id = $id AND status = 'Completed';
            """;
        command.Parameters.AddWithValue("$id", indexId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                   symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                   symbol.body_recovery_status
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY symbol.canonical_key COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexSymbolRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSymbol(reader));
        return result;
    }

    public async Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                   symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                   symbol.body_recovery_status
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND symbol.symbol_id = $symbolId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSymbol(reader) : null;
    }

    public async Task<int> CountCompletedSymbolMatchesAsync(
        string indexId,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND (
                  symbol.qualified_name LIKE $contains ESCAPE '\' COLLATE NOCASE
                  OR symbol.signature LIKE $contains ESCAPE '\' COLLATE NOCASE
              );
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$contains", "%" + EscapeLikePattern(query) + "%");
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(
        string indexId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The symbol search limit must be positive.");

        var escaped = EscapeLikePattern(query);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                   symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                   symbol.body_recovery_status
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND (
                  symbol.qualified_name LIKE $contains ESCAPE '\' COLLATE NOCASE
                  OR symbol.signature LIKE $contains ESCAPE '\' COLLATE NOCASE
              )
            ORDER BY
                CASE
                    WHEN symbol.qualified_name = $query COLLATE NOCASE
                      OR symbol.signature = $query COLLATE NOCASE THEN 0
                    WHEN symbol.qualified_name LIKE $terminal ESCAPE '\' COLLATE NOCASE THEN 1
                    WHEN symbol.qualified_name LIKE $prefix ESCAPE '\' COLLATE NOCASE THEN 2
                    WHEN symbol.qualified_name LIKE $contains ESCAPE '\' COLLATE NOCASE THEN 3
                    ELSE 4
                END,
                symbol.qualified_name COLLATE BINARY,
                symbol.signature COLLATE BINARY,
                symbol.symbol_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$terminal", "%." + escaped);
        command.Parameters.AddWithValue("$prefix", escaped + "%");
        command.Parameters.AddWithValue("$contains", "%" + escaped + "%");
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<IndexSymbolRecord>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSymbol(reader));
        return result;
    }

    public async Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                   relationship.target_symbol_id, relationship.target_text, relationship.relationship_kind, relationship.evidence
            FROM relationships AS relationship
            INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY relationship.relationship_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexRelationshipRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexRelationshipRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return result;
    }

    public async Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file.source_file_id, file.snapshot_id, file.relative_path, file.sha256, file.byte_count
            FROM source_files AS file
            INNER JOIN index_runs AS run ON run.snapshot_id = file.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY file.relative_path COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexSourceFileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexSourceFileRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        return result;
    }

    public async Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT location.symbol_id, location.source_file_id, location.start_line, location.start_column,
                   location.end_line, location.end_column
            FROM source_locations AS location
            INNER JOIN symbols AS symbol ON symbol.symbol_id = location.symbol_id
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY location.source_file_id COLLATE BINARY, location.start_line, location.start_column, location.symbol_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexSourceLocationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexSourceLocationRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        return result;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void AddSnapshotParameters(SqliteCommand command, CodeSnapshotRecord snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$codebase", snapshot.Codebase.ToString());
        command.Parameters.AddWithValue("$channel", snapshot.Channel.ToString());
        command.Parameters.AddWithValue("$environment", (object?)snapshot.EnvironmentSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", snapshot.SourceIdentity);
        command.Parameters.AddWithValue("$created", snapshot.CreatedAtUtc);
    }

    private static async Task<string?> GetRunningSnapshotIdAsync(SqliteConnection connection, SqliteTransaction transaction, string indexId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot_id FROM index_runs WHERE index_id = $id AND status = 'Running';";
        command.Parameters.AddWithValue("$id", indexId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task InsertSymbolAsync(SqliteConnection connection, SqliteTransaction transaction, IndexSymbolRecord symbol, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort, body_recovery_status) VALUES ($id,$snapshot,$key,$kind,$name,$signature,$best,$bodyRecovery);";
        command.Parameters.AddWithValue("$id", symbol.SymbolId);
        command.Parameters.AddWithValue("$snapshot", symbol.SnapshotId);
        command.Parameters.AddWithValue("$key", symbol.CanonicalKey);
        command.Parameters.AddWithValue("$kind", symbol.Kind);
        command.Parameters.AddWithValue("$name", symbol.QualifiedName);
        command.Parameters.AddWithValue("$signature", symbol.Signature);
        command.Parameters.AddWithValue("$best", symbol.IsBestEffort ? 1 : 0);
        command.Parameters.AddWithValue("$bodyRecovery", symbol.BodyRecoveryStatus?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSourceFileAsync(SqliteConnection connection, SqliteTransaction transaction, IndexSourceFileRecord file, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_files(source_file_id, snapshot_id, relative_path, sha256, byte_count) VALUES ($id,$snapshot,$path,$sha,$bytes);";
        command.Parameters.AddWithValue("$id", file.SourceFileId); command.Parameters.AddWithValue("$snapshot", file.SnapshotId); command.Parameters.AddWithValue("$path", file.RelativePath); command.Parameters.AddWithValue("$sha", file.Sha256); command.Parameters.AddWithValue("$bytes", file.ByteCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSourceLocationAsync(SqliteConnection connection, SqliteTransaction transaction, IndexSourceLocationRecord location, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_locations(symbol_id, source_file_id, start_line, start_column, end_line, end_column) VALUES ($symbol,$file,$line,$column,$endLine,$endColumn);";
        command.Parameters.AddWithValue("$symbol", location.SymbolId); command.Parameters.AddWithValue("$file", location.SourceFileId); command.Parameters.AddWithValue("$line", location.StartLine); command.Parameters.AddWithValue("$column", location.StartColumn); command.Parameters.AddWithValue("$endLine", (object?)location.EndLine ?? DBNull.Value); command.Parameters.AddWithValue("$endColumn", (object?)location.EndColumn ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFingerprintAsync(SqliteConnection connection, SqliteTransaction transaction, IndexFingerprintRecord fingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbol_fingerprints(symbol_id, fingerprint_kind, fingerprint) VALUES ($symbol,$kind,$fingerprint);";
        command.Parameters.AddWithValue("$symbol", fingerprint.SymbolId); command.Parameters.AddWithValue("$kind", fingerprint.Kind); command.Parameters.AddWithValue("$fingerprint", fingerprint.Fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRelationshipAsync(SqliteConnection connection, SqliteTransaction transaction, IndexRelationshipRecord relationship, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO relationships(relationship_id, snapshot_id, source_symbol_id, target_symbol_id, target_text, relationship_kind, evidence) VALUES ($id,$snapshot,$source,$target,$text,$kind,$evidence);";
        command.Parameters.AddWithValue("$id", relationship.RelationshipId); command.Parameters.AddWithValue("$snapshot", relationship.SnapshotId); command.Parameters.AddWithValue("$source", relationship.SourceSymbolId); command.Parameters.AddWithValue("$target", (object?)relationship.TargetSymbolId ?? DBNull.Value); command.Parameters.AddWithValue("$text", (object?)relationship.TargetText ?? DBNull.Value); command.Parameters.AddWithValue("$kind", relationship.Kind); command.Parameters.AddWithValue("$evidence", relationship.Evidence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CodeSnapshotRecord ReadSnapshot(SqliteDataReader reader) =>
        new(reader.GetString(0), Enum.Parse<CodebaseKind>(reader.GetString(1)), Enum.Parse<CodeChannel>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static IndexRunRecord ReadRun(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), Enum.Parse<IndexRunStatus>(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static IndexSymbolRecord ReadSymbol(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6) != 0,
            reader.IsDBNull(7) ? null : Enum.Parse<BodyRecoveryStatus>(reader.GetString(7)));
}
