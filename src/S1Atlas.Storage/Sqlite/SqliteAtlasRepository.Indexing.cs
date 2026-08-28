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
            var runningSnapshot = await GetRunningSnapshotAsync(connection, transaction, indexId, cancellationToken)
                ?? throw new InvalidOperationException($"Index run '{indexId}' is not running.");
            var snapshotId = runningSnapshot.SnapshotId;

            if (writeSet.Symbols.Any(symbol => !string.Equals(symbol.SnapshotId, snapshotId, StringComparison.Ordinal)) ||
                writeSet.SourceFiles.Any(file => !string.Equals(file.SnapshotId, snapshotId, StringComparison.Ordinal)) ||
                writeSet.Relationships.Any(edge => !string.Equals(edge.SnapshotId, snapshotId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Index write-set rows must belong to the running snapshot.");

            ValidateCallableSurfaceOwnership(indexId, snapshotId, writeSet.Symbols, writeSet.CallableSurface ?? []);
            var referenceWriteSet = await ValidateReferenceWriteSetAsync(
                connection,
                transaction,
                indexId,
                runningSnapshot,
                writeSet,
                cancellationToken);

            await InsertSourceFilesAsync(connection, transaction, writeSet.SourceFiles, cancellationToken);
            await InsertSymbolsAsync(connection, transaction, writeSet.Symbols, cancellationToken);
            await InsertReferenceIndexContextAsync(connection, transaction, referenceWriteSet, cancellationToken);
            await InsertReferenceModsAsync(connection, transaction, indexId, snapshotId, referenceWriteSet?.Mods ?? [], cancellationToken);
            await InsertReferenceDocumentsAsync(connection, transaction, indexId, snapshotId, referenceWriteSet?.Documents ?? [], cancellationToken);
            await InsertReferenceSymbolOwnersAsync(connection, transaction, indexId, snapshotId, referenceWriteSet?.Mods ?? [], cancellationToken);
            await InsertSourceLocationsAsync(connection, transaction, writeSet.SourceLocations, cancellationToken);
            await InsertFingerprintsAsync(connection, transaction, writeSet.Fingerprints, cancellationToken);
            await InsertRelationshipsAsync(connection, transaction, writeSet.Relationships, cancellationToken);
            await InsertCallableSurfaceAsync(connection, transaction, writeSet.CallableSurface ?? [], cancellationToken);

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
                   symbol.body_recovery_status, symbol.is_public
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

    public async Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolPageAsync(
        string indexId, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                   symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                   symbol.body_recovery_status, symbol.is_public
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY symbol.canonical_key COLLATE BINARY,
                     symbol.kind COLLATE BINARY,
                     symbol.symbol_id COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<IndexSymbolRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSymbol(reader));
        return result;
    }

    public async Task<int> CountCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed';
            """;
        command.Parameters.AddWithValue("$id", indexId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(
        string indexId,
        string canonicalKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                   symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                   symbol.body_recovery_status, symbol.is_public
            FROM index_runs AS run
            INNER JOIN symbols AS symbol INDEXED BY ux_symbols_snapshot_key
                ON symbol.snapshot_id = run.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND symbol.canonical_key = $canonicalKey COLLATE BINARY
            ORDER BY symbol.symbol_id COLLATE BINARY
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$canonicalKey", canonicalKey);

        var result = new List<IndexSymbolRecord>(2);
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
                   symbol.body_recovery_status, symbol.is_public
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
        CancellationToken cancellationToken,
        string? kind = null)
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
              AND ($kind IS NULL OR symbol.kind = $kind)
              AND (
                  symbol.qualified_name LIKE $contains ESCAPE '\' COLLATE NOCASE
                  OR symbol.signature LIKE $contains ESCAPE '\' COLLATE NOCASE
              );
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$contains", "%" + EscapeLikePattern(query) + "%");
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(
        string indexId,
        string query,
        int limit,
        CancellationToken cancellationToken,
        string? kind = null)
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
                   symbol.body_recovery_status, symbol.is_public
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND ($kind IS NULL OR symbol.kind = $kind)
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
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
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

    private static async Task<RunningSnapshotRecord?> GetRunningSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot.snapshot_id, snapshot.codebase, snapshot.channel, snapshot.environment_snapshot_id
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot
                ON snapshot.snapshot_id = run.snapshot_id
            WHERE run.index_id = $id
              AND run.status = 'Running'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RunningSnapshotRecord(
                reader.GetString(0),
                Enum.Parse<CodebaseKind>(reader.GetString(1)),
                Enum.Parse<CodeChannel>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    // Prepared, reusable-command inserts (AT-7). One compiled statement per table, parameter
    // objects created once and re-valued per row — eliminates the per-row CreateCommand + SQL
    // re-parse that dominated first-index allocation. Ordering and the enclosing transaction are
    // unchanged, so output content and atomic publication are identical to the per-row path.
    private static async Task InsertSymbolsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IndexSymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort, body_recovery_status, is_public) VALUES ($id,$snapshot,$key,$kind,$name,$signature,$best,$bodyRecovery,$isPublic);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var signature = command.Parameters.Add("$signature", SqliteType.Text);
        var best = command.Parameters.Add("$best", SqliteType.Integer);
        var bodyRecovery = command.Parameters.Add("$bodyRecovery", SqliteType.Text);
        var isPublic = command.Parameters.Add("$isPublic", SqliteType.Integer);
        command.Prepare();
        foreach (var symbol in symbols)
        {
            id.Value = symbol.SymbolId;
            snapshot.Value = symbol.SnapshotId;
            key.Value = symbol.CanonicalKey;
            kind.Value = symbol.Kind;
            name.Value = symbol.QualifiedName;
            signature.Value = symbol.Signature;
            best.Value = symbol.IsBestEffort ? 1 : 0;
            bodyRecovery.Value = symbol.BodyRecoveryStatus?.ToString() ?? (object)DBNull.Value;
            isPublic.Value = symbol.IsPublic ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCallableSurfaceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<IndexCallableSurfaceRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO callable_surface(callable_surface_id, index_id, snapshot_id, game_symbol_id, game_canonical_key, interop_assembly_name, interop_input_sha256, interop_signature, callable_kind, requires_reflection, status, interop_input_trust, evidence) VALUES ($id,$index,$snapshot,$symbol,$key,$assembly,$hash,$signature,$kind,$reflection,$status,$trust,$evidence);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var index = command.Parameters.Add("$index", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        var symbol = command.Parameters.Add("$symbol", SqliteType.Text);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var assembly = command.Parameters.Add("$assembly", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        var signature = command.Parameters.Add("$signature", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var reflection = command.Parameters.Add("$reflection", SqliteType.Integer);
        var status = command.Parameters.Add("$status", SqliteType.Text);
        var trust = command.Parameters.Add("$trust", SqliteType.Text);
        var evidence = command.Parameters.Add("$evidence", SqliteType.Text);
        command.Prepare();
        foreach (var record in records)
        {
            id.Value = record.CallableSurfaceId;
            index.Value = record.IndexId;
            snapshot.Value = record.SnapshotId;
            symbol.Value = record.GameSymbolId;
            key.Value = record.GameCanonicalKey;
            assembly.Value = record.InteropAssemblyName;
            hash.Value = (object?)record.InteropInputSha256 ?? DBNull.Value;
            signature.Value = (object?)record.InteropSignature ?? DBNull.Value;
            kind.Value = record.Kind.ToString();
            reflection.Value = record.RequiresReflection ? 1 : 0;
            status.Value = record.Status.ToString();
            trust.Value = record.InteropInputTrust.ToString();
            evidence.Value = record.Evidence;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateCallableSurfaceOwnership(
        string indexId,
        string snapshotId,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyList<IndexCallableSurfaceRecord> records)
    {
        var symbolsById = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!string.Equals(record.IndexId, indexId, StringComparison.Ordinal) ||
                !string.Equals(record.SnapshotId, snapshotId, StringComparison.Ordinal))
                throw new InvalidOperationException("Callable-surface rows must belong to the running index and snapshot.");

            if (!symbolsById.TryGetValue(record.GameSymbolId, out var symbol) ||
                !string.Equals(symbol.SnapshotId, snapshotId, StringComparison.Ordinal) ||
                !string.Equals(symbol.CanonicalKey, record.GameCanonicalKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Callable-surface rows must reference a matching symbol in the running snapshot.");
        }
    }

    private static async Task InsertSourceFilesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IndexSourceFileRecord> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_files(source_file_id, snapshot_id, relative_path, sha256, byte_count) VALUES ($id,$snapshot,$path,$sha,$bytes);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var sha = command.Parameters.Add("$sha", SqliteType.Text);
        var bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
        command.Prepare();
        foreach (var file in files)
        {
            id.Value = file.SourceFileId;
            snapshot.Value = file.SnapshotId;
            path.Value = file.RelativePath;
            sha.Value = file.Sha256;
            bytes.Value = file.ByteCount;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertSourceLocationsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IndexSourceLocationRecord> locations, CancellationToken cancellationToken)
    {
        if (locations.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_locations(symbol_id, source_file_id, start_line, start_column, end_line, end_column) VALUES ($symbol,$file,$line,$column,$endLine,$endColumn);";
        var symbol = command.Parameters.Add("$symbol", SqliteType.Text);
        var file = command.Parameters.Add("$file", SqliteType.Text);
        var line = command.Parameters.Add("$line", SqliteType.Integer);
        var column = command.Parameters.Add("$column", SqliteType.Integer);
        var endLine = command.Parameters.Add("$endLine", SqliteType.Integer);
        var endColumn = command.Parameters.Add("$endColumn", SqliteType.Integer);
        command.Prepare();
        foreach (var location in locations)
        {
            symbol.Value = location.SymbolId;
            file.Value = location.SourceFileId;
            line.Value = location.StartLine;
            column.Value = location.StartColumn;
            endLine.Value = (object?)location.EndLine ?? DBNull.Value;
            endColumn.Value = (object?)location.EndColumn ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertFingerprintsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IndexFingerprintRecord> fingerprints, CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbol_fingerprints(symbol_id, fingerprint_kind, fingerprint) VALUES ($symbol,$kind,$fingerprint);";
        var symbol = command.Parameters.Add("$symbol", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var fingerprint = command.Parameters.Add("$fingerprint", SqliteType.Text);
        command.Prepare();
        foreach (var record in fingerprints)
        {
            symbol.Value = record.SymbolId;
            kind.Value = record.Kind;
            fingerprint.Value = record.Fingerprint;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertReferenceIndexContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedReferenceWriteSet? referenceWriteSet,
        CancellationToken cancellationToken)
    {
        if (referenceWriteSet is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_index_context(
                reference_index_id,
                reference_snapshot_id,
                game_index_id,
                game_snapshot_id,
                build_id)
            VALUES ($referenceIndex,$referenceSnapshot,$gameIndex,$gameSnapshot,$buildId);
            """;
        command.Parameters.AddWithValue("$referenceIndex", referenceWriteSet.Context.ReferenceIndexId);
        command.Parameters.AddWithValue("$referenceSnapshot", referenceWriteSet.ReferenceSnapshotId);
        command.Parameters.AddWithValue("$gameIndex", referenceWriteSet.Context.GameIndexId);
        command.Parameters.AddWithValue("$gameSnapshot", referenceWriteSet.GameSnapshotId);
        command.Parameters.AddWithValue("$buildId", referenceWriteSet.Context.BuildId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReferenceModsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        string snapshotId,
        IReadOnlyList<IndexReferenceModRecord> mods,
        CancellationToken cancellationToken)
    {
        if (mods.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_mods(
                index_id,
                snapshot_id,
                mod_id,
                display_name,
                version,
                license,
                root_path,
                content_sha256)
            VALUES ($indexId,$snapshotId,$modId,$displayName,$version,$license,$rootPath,$contentSha256);
            """;
        var index = command.Parameters.Add("$indexId", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshotId", SqliteType.Text);
        var modId = command.Parameters.Add("$modId", SqliteType.Text);
        var displayName = command.Parameters.Add("$displayName", SqliteType.Text);
        var version = command.Parameters.Add("$version", SqliteType.Text);
        var license = command.Parameters.Add("$license", SqliteType.Text);
        var rootPath = command.Parameters.Add("$rootPath", SqliteType.Text);
        var contentSha256 = command.Parameters.Add("$contentSha256", SqliteType.Text);
        command.Prepare();
        foreach (var mod in mods)
        {
            index.Value = indexId;
            snapshot.Value = snapshotId;
            modId.Value = mod.ModId;
            displayName.Value = mod.DisplayName;
            version.Value = mod.Version;
            license.Value = (object?)mod.License ?? DBNull.Value;
            rootPath.Value = mod.RootPath;
            contentSha256.Value = mod.ContentSha256;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertReferenceDocumentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        string snapshotId,
        IReadOnlyList<IndexReferenceDocumentRecord> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_documents(
                index_id,
                snapshot_id,
                mod_id,
                relative_path,
                kind,
                sha256,
                byte_count,
                content)
            VALUES ($indexId,$snapshotId,$modId,$relativePath,$kind,$sha256,$byteCount,$content);
            """;
        var index = command.Parameters.Add("$indexId", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshotId", SqliteType.Text);
        var modId = command.Parameters.Add("$modId", SqliteType.Text);
        var relativePath = command.Parameters.Add("$relativePath", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var sha256 = command.Parameters.Add("$sha256", SqliteType.Text);
        var byteCount = command.Parameters.Add("$byteCount", SqliteType.Integer);
        var content = command.Parameters.Add("$content", SqliteType.Text);
        command.Prepare();
        foreach (var document in documents)
        {
            index.Value = indexId;
            snapshot.Value = snapshotId;
            modId.Value = document.ModId;
            relativePath.Value = document.RelativePath;
            kind.Value = document.Kind;
            sha256.Value = document.Sha256;
            byteCount.Value = document.ByteCount;
            content.Value = document.Content;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertReferenceSymbolOwnersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        string snapshotId,
        IReadOnlyList<IndexReferenceModRecord> mods,
        CancellationToken cancellationToken)
    {
        if (mods.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_symbol_owners(index_id, snapshot_id, symbol_id, mod_id)
            VALUES ($indexId,$snapshotId,$symbolId,$modId);
            """;
        var index = command.Parameters.Add("$indexId", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshotId", SqliteType.Text);
        var symbolId = command.Parameters.Add("$symbolId", SqliteType.Text);
        var modId = command.Parameters.Add("$modId", SqliteType.Text);
        command.Prepare();
        foreach (var mod in mods)
        {
            foreach (var ownedSymbolId in mod.SymbolIds.Distinct(StringComparer.Ordinal))
            {
                index.Value = indexId;
                snapshot.Value = snapshotId;
                symbolId.Value = ownedSymbolId;
                modId.Value = mod.ModId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task InsertRelationshipsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IndexRelationshipRecord> relationships, CancellationToken cancellationToken)
    {
        if (relationships.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO relationships(relationship_id, snapshot_id, source_symbol_id, target_symbol_id, target_text, relationship_kind, evidence) VALUES ($id,$snapshot,$source,$target,$text,$kind,$evidence);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var target = command.Parameters.Add("$target", SqliteType.Text);
        var text = command.Parameters.Add("$text", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var evidence = command.Parameters.Add("$evidence", SqliteType.Text);
        command.Prepare();
        foreach (var relationship in relationships)
        {
            id.Value = relationship.RelationshipId;
            snapshot.Value = relationship.SnapshotId;
            source.Value = relationship.SourceSymbolId;
            target.Value = (object?)relationship.TargetSymbolId ?? DBNull.Value;
            text.Value = (object?)relationship.TargetText ?? DBNull.Value;
            kind.Value = relationship.Kind;
            evidence.Value = relationship.Evidence;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fp.symbol_id, fp.fingerprint_kind, fp.fingerprint
            FROM symbol_fingerprints AS fp
            INNER JOIN symbols AS symbol ON symbol.symbol_id = fp.symbol_id
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $id AND run.status = 'Completed'
            ORDER BY fp.symbol_id COLLATE BINARY, fp.fingerprint_kind COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexFingerprintRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexFingerprintRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT callable.callable_surface_id, callable.index_id, callable.snapshot_id,
                   callable.game_symbol_id, callable.game_canonical_key, callable.interop_assembly_name,
                   callable.interop_input_sha256, callable.interop_signature, callable.callable_kind,
                   callable.requires_reflection, callable.status, callable.interop_input_trust, callable.evidence
            FROM callable_surface AS callable
            INNER JOIN index_runs AS run ON run.index_id = callable.index_id
            WHERE callable.index_id = $id AND run.status = 'Completed'
            ORDER BY callable.game_canonical_key COLLATE BINARY, callable.callable_surface_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", indexId);
        var result = new List<IndexCallableSurfaceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadCallableSurface(reader));
        return result;
    }

    public async Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceByGameSymbolIdAsync(
        string indexId,
        string gameSymbolId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSymbolId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT callable.callable_surface_id, callable.index_id, callable.snapshot_id,
                   callable.game_symbol_id, callable.game_canonical_key, callable.interop_assembly_name,
                   callable.interop_input_sha256, callable.interop_signature, callable.callable_kind,
                   callable.requires_reflection, callable.status, callable.interop_input_trust, callable.evidence
            FROM callable_surface AS callable
            INNER JOIN index_runs AS run
                ON run.index_id = callable.index_id
               AND run.snapshot_id = callable.snapshot_id
            INNER JOIN symbols AS symbol
                ON symbol.symbol_id = callable.game_symbol_id
               AND symbol.snapshot_id = run.snapshot_id
               AND symbol.canonical_key = callable.game_canonical_key
            WHERE callable.index_id = $indexId
              AND callable.game_symbol_id = $gameSymbolId
              AND run.status = 'Completed'
            ORDER BY callable.callable_surface_id COLLATE BINARY
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$gameSymbolId", gameSymbolId);
        var result = new List<IndexCallableSurfaceRecord>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadCallableSurface(reader));
        return result;
    }

    public async Task<ReferenceIndexContextRecord?> GetReferenceIndexContextAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT context.reference_index_id, context.game_index_id, context.build_id
            FROM reference_index_context AS context
            INNER JOIN index_runs AS run
                ON run.index_id = context.reference_index_id
               AND run.snapshot_id = context.reference_snapshot_id
            WHERE context.reference_index_id = $indexId
              AND run.status = 'Completed'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReferenceIndexContextRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<IReadOnlyList<IndexReferenceModRecord>> GetCompletedReferenceModsAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var ownedSymbols = await LoadReferenceSymbolOwnersByModAsync(connection, indexId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mod.mod_id, mod.display_name, mod.version, mod.license, mod.root_path, mod.content_sha256
            FROM reference_mods AS mod
            INNER JOIN index_runs AS run
                ON run.index_id = mod.index_id
               AND run.snapshot_id = mod.snapshot_id
            WHERE mod.index_id = $indexId
              AND run.status = 'Completed'
            ORDER BY mod.mod_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        var result = new List<IndexReferenceModRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var modId = reader.GetString(0);
            result.Add(new IndexReferenceModRecord(
                modId,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ownedSymbols.TryGetValue(modId, out var symbolIds) ? symbolIds : []));
        }

        return result;
    }

    public async Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document.mod_id, document.relative_path, document.kind, document.sha256,
                   document.byte_count, document.content
            FROM reference_documents AS document
            INNER JOIN index_runs AS run
                ON run.index_id = document.index_id
               AND run.snapshot_id = document.snapshot_id
            WHERE document.index_id = $indexId
              AND run.status = 'Completed'
            ORDER BY document.mod_id COLLATE BINARY, document.relative_path COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        var result = new List<IndexReferenceDocumentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadReferenceDocument(reader));
        return result;
    }

    public async Task<IReadOnlyList<IndexReferenceDocumentRecord>> SearchCompletedReferenceDocumentsAsync(
        string indexId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The reference document search limit must be positive.");

        var escaped = EscapeLikePattern(query);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document.mod_id, document.relative_path, document.kind, document.sha256,
                   document.byte_count, document.content
            FROM reference_documents AS document
            INNER JOIN index_runs AS run
                ON run.index_id = document.index_id
               AND run.snapshot_id = document.snapshot_id
            WHERE document.index_id = $indexId
              AND run.status = 'Completed'
              AND (
                  document.relative_path LIKE $contains ESCAPE '\' COLLATE NOCASE
                  OR document.content LIKE $contains ESCAPE '\' COLLATE NOCASE
              )
            ORDER BY
                CASE
                    WHEN document.relative_path = $query COLLATE NOCASE THEN 0
                    WHEN document.relative_path LIKE $prefix ESCAPE '\' COLLATE NOCASE THEN 1
                    WHEN document.relative_path LIKE $contains ESCAPE '\' COLLATE NOCASE THEN 2
                    ELSE 3
                END,
                document.relative_path COLLATE BINARY,
                document.mod_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$prefix", escaped + "%");
        command.Parameters.AddWithValue("$contains", "%" + escaped + "%");
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<IndexReferenceDocumentRecord>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadReferenceDocument(reader));
        return result;
    }

    public async Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
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
              AND snapshot.source_identity = $sourceIdentity
            ORDER BY run.completed_at_utc DESC, run.index_id COLLATE BINARY DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$codebase", codebase.ToString());
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$sourceIdentity", sourceIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run.index_id, run.snapshot_id, run.status, run.started_at_utc,
                   run.completed_at_utc, run.failure_message
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot ON snapshot.snapshot_id = run.snapshot_id
            INNER JOIN environment_snapshots AS env ON env.snapshot_id = snapshot.environment_snapshot_id
            WHERE run.status = 'Completed'
              AND snapshot.codebase = $codebase
              AND snapshot.channel = $channel
              AND env.build_id = $buildId
            ORDER BY run.completed_at_utc DESC, run.index_id COLLATE BINARY DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$codebase", codebase.ToString());
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$buildId", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<string?> GetCompletedIndexBuildIdAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT env.build_id
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot ON snapshot.snapshot_id = run.snapshot_id
            INNER JOIN environment_snapshots AS env ON env.snapshot_id = snapshot.environment_snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
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
            reader.IsDBNull(7) ? null : Enum.Parse<BodyRecoveryStatus>(reader.GetString(7)),
            reader.GetInt64(8) != 0);

    private static IndexCallableSurfaceRecord ReadCallableSurface(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Enum.Parse<CallableSurfaceKind>(reader.GetString(8)),
            reader.GetInt64(9) != 0,
            Enum.Parse<CallableSurfaceStatus>(reader.GetString(10)),
            Enum.Parse<InteropInputTrust>(reader.GetString(11)),
            reader.GetString(12));

    private static IndexReferenceDocumentRecord ReadReferenceDocument(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5));

    private static async Task<ValidatedReferenceWriteSet?> ValidateReferenceWriteSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        RunningSnapshotRecord runningSnapshot,
        IndexWriteSet writeSet,
        CancellationToken cancellationToken)
    {
        var mods = writeSet.ReferenceMods ?? [];
        var documents = writeSet.ReferenceDocuments ?? [];
        var hasReferenceRows = writeSet.ReferenceIndexContext is not null || mods.Count > 0 || documents.Count > 0;
        if (runningSnapshot.Codebase != CodebaseKind.ReferenceMod)
        {
            if (hasReferenceRows)
                throw new InvalidOperationException("Only reference-mod snapshots can persist reference mod rows.");
            return null;
        }

        if (runningSnapshot.Channel != CodeChannel.Installed)
            throw new InvalidOperationException("Reference-mod snapshots must use the Installed channel.");

        var context = writeSet.ReferenceIndexContext
            ?? throw new InvalidOperationException("Reference-mod indexes require a reference index context.");
        if (!string.Equals(context.ReferenceIndexId, indexId, StringComparison.Ordinal))
            throw new InvalidOperationException("Reference index context must belong to the running index.");

        var gameIndex = await GetCompletedIndexOwnershipAsync(
            connection,
            transaction,
            context.GameIndexId,
            cancellationToken) ?? throw new InvalidOperationException("Reference-mod indexes require a completed base game index.");
        if (gameIndex.Codebase != CodebaseKind.ScheduleI || gameIndex.Channel != CodeChannel.Installed)
            throw new InvalidOperationException("Reference-mod indexes must target a completed installed Schedule I index.");
        if (gameIndex.BuildId is not null && !string.Equals(gameIndex.BuildId, context.BuildId, StringComparison.Ordinal))
            throw new InvalidOperationException("Reference index context build id must match the completed base game index build.");

        var referenceBuildId = await GetBuildIdForEnvironmentSnapshotAsync(
            connection,
            transaction,
            runningSnapshot.EnvironmentSnapshotId,
            cancellationToken);
        if (referenceBuildId is not null && !string.Equals(referenceBuildId, context.BuildId, StringComparison.Ordinal))
            throw new InvalidOperationException("Reference-mod snapshots must match the recorded base game build.");

        var symbolsById = writeSet.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var persistedReferenceSymbolIds = await LoadSymbolIdsForSnapshotAsync(
            connection,
            transaction,
            runningSnapshot.SnapshotId,
            cancellationToken);
        var modIds = new HashSet<string>(StringComparer.Ordinal);
        var ownedReferenceSymbolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            if (!modIds.Add(mod.ModId))
                throw new InvalidOperationException("Reference mod ids must be unique within an index.");

            foreach (var ownedSymbolId in mod.SymbolIds)
            {
                if (!ownedReferenceSymbolIds.Add(ownedSymbolId))
                    throw new InvalidOperationException("Every reference source symbol must have exactly one mod owner.");

                if (!symbolsById.TryGetValue(ownedSymbolId, out var ownedSymbol) ||
                    !string.Equals(ownedSymbol.SnapshotId, runningSnapshot.SnapshotId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Reference source symbols must belong to the running reference snapshot.");
            }
        }

        if (persistedReferenceSymbolIds.Any(symbolId => !ownedReferenceSymbolIds.Contains(symbolId)) ||
            symbolsById.Keys.Any(symbolId => !ownedReferenceSymbolIds.Contains(symbolId)))
            throw new InvalidOperationException("Every reference source symbol must have exactly one mod owner.");

        foreach (var document in documents)
        {
            if (!modIds.Contains(document.ModId))
                throw new InvalidOperationException("Reference documents must belong to a persisted reference mod.");
        }

        foreach (var relationship in writeSet.Relationships)
        {
            if (!symbolsById.TryGetValue(relationship.SourceSymbolId, out var sourceSymbol) ||
                !string.Equals(sourceSymbol.SnapshotId, runningSnapshot.SnapshotId, StringComparison.Ordinal))
                throw new InvalidOperationException("Reference relationships must originate from the running reference snapshot.");
        }

        var externalTargets = writeSet.Relationships
            .Where(relationship => relationship.TargetSymbolId is not null)
            .Select(relationship => relationship.TargetSymbolId!)
            .Distinct(StringComparer.Ordinal)
            .Where(symbolId => !symbolsById.ContainsKey(symbolId))
            .ToArray();
        var externalTargetSnapshots = await LoadSymbolSnapshotIdsAsync(
            connection,
            transaction,
            externalTargets,
            cancellationToken);
        foreach (var relationship in writeSet.Relationships)
        {
            if (relationship.TargetSymbolId is null)
                continue;

            if (symbolsById.TryGetValue(relationship.TargetSymbolId, out var targetSymbol))
            {
                if (!string.Equals(targetSymbol.SnapshotId, runningSnapshot.SnapshotId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Reference relationships may only target the running snapshot or recorded base game index.");
                continue;
            }

            if (!externalTargetSnapshots.TryGetValue(relationship.TargetSymbolId, out var targetSnapshotId) ||
                !string.Equals(targetSnapshotId, gameIndex.SnapshotId, StringComparison.Ordinal))
                throw new InvalidOperationException("Reference relationships may only target the running snapshot or recorded base game index.");
        }

        return new ValidatedReferenceWriteSet(
            context,
            runningSnapshot.SnapshotId,
            gameIndex.SnapshotId,
            mods,
            documents);
    }

    private static async Task<CompletedIndexOwnershipRecord?> GetCompletedIndexOwnershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot.snapshot_id, snapshot.codebase, snapshot.channel, env.build_id
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot
                ON snapshot.snapshot_id = run.snapshot_id
            LEFT JOIN environment_snapshots AS env
                ON env.snapshot_id = snapshot.environment_snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CompletedIndexOwnershipRecord(
                reader.GetString(0),
                Enum.Parse<CodebaseKind>(reader.GetString(1)),
                Enum.Parse<CodeChannel>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async Task<string?> GetBuildIdForEnvironmentSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? environmentSnapshotId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentSnapshotId))
            return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT build_id
            FROM environment_snapshots
            WHERE snapshot_id = $snapshotId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshotId", environmentSnapshotId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<Dictionary<string, string>> LoadSymbolSnapshotIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (symbolIds.Count == 0)
            return result;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new string[symbolIds.Count];
        for (var index = 0; index < symbolIds.Count; index++)
        {
            parameterNames[index] = "$symbol" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(parameterNames[index], symbolIds[index]);
        }

        command.CommandText = $"""
            SELECT symbol_id, snapshot_id
            FROM symbols
            WHERE symbol_id IN ({string.Join(", ", parameterNames)});
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<IReadOnlyList<string>> LoadSymbolIdsForSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT symbol_id
            FROM symbols
            WHERE snapshot_id = $snapshotId;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> LoadReferenceSymbolOwnersByModAsync(
        SqliteConnection connection,
        string indexId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner.mod_id, owner.symbol_id
            FROM reference_symbol_owners AS owner
            INNER JOIN index_runs AS run
                ON run.index_id = owner.index_id
               AND run.snapshot_id = owner.snapshot_id
            WHERE owner.index_id = $indexId
              AND run.status = 'Completed'
            ORDER BY owner.mod_id COLLATE BINARY, owner.symbol_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var modId = reader.GetString(0);
            if (!result.TryGetValue(modId, out var symbolIds))
            {
                symbolIds = [];
                result.Add(modId, symbolIds);
            }

            symbolIds.Add(reader.GetString(1));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private sealed record RunningSnapshotRecord(
        string SnapshotId,
        CodebaseKind Codebase,
        CodeChannel Channel,
        string? EnvironmentSnapshotId);

    private sealed record CompletedIndexOwnershipRecord(
        string SnapshotId,
        CodebaseKind Codebase,
        CodeChannel Channel,
        string? BuildId);

    private sealed record ValidatedReferenceWriteSet(
        ReferenceIndexContextRecord Context,
        string ReferenceSnapshotId,
        string GameSnapshotId,
        IReadOnlyList<IndexReferenceModRecord> Mods,
        IReadOnlyList<IndexReferenceDocumentRecord> Documents);
}
