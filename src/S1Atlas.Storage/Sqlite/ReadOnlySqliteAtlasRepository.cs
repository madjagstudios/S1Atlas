using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;

namespace S1Atlas.Storage.Sqlite;

public sealed class ReadOnlySqliteAtlasRepository :
    IAtlasRepository,
    IIndexRepository,
    ISceneRepository,
    IValidatedExtractionRepository
{
    private const string ReadOnlyMessage = "S1Atlas MCP is read-only.";
    private const string AttemptColumns = """
        attempt_id,
        recipe_id,
        build_id,
        tool_instance_id,
        profile_id,
        profile_version,
        profile_digest,
        validation_policy_id,
        validation_policy_version,
        validation_policy_digest,
        adapter_version,
        extraction_schema_version,
        input_source,
        input_snapshot_id,
        status,
        created_at_utc,
        started_at_utc,
        completed_at_utc,
        pre_input_manifest_digest,
        post_input_manifest_digest,
        working_path,
        stdout_path,
        stderr_path,
        stdout_truncated,
        stderr_truncated,
        stdout_discarded_bytes,
        stderr_discarded_bytes,
        process_id,
        process_exit_code,
        failure_stage,
        failure_code,
        failure_message,
        keep_failed_artifacts,
        discarded_file_count,
        discarded_byte_count,
        candidate_output_path,
        result_extraction_id,
        validation_source_extraction_id
        """;
    private const string ValidatedExtractionColumns = """
        extraction_id,
        recipe_id,
        build_id,
        tool_instance_id,
        source_attempt_id,
        profile_id,
        profile_version,
        profile_digest,
        adapter_version,
        extraction_schema_version,
        artifact_manifest_digest,
        root_path,
        created_at_utc,
        trust_level,
        validation_outcome,
        artifact_count,
        library_count,
        managed_assembly_count,
        type_count,
        method_count,
        field_count,
        property_count,
        event_count,
        total_output_bytes,
        total_managed_bytes
        """;
    private const string SnapshotSelectSql = """
        SELECT scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id,
               code_index_id, parser_id, parser_version, container_manifest_digest, status,
               recovery_status, started_at_utc, completed_at_utc, failure_code, failure_message
        FROM scene_snapshots
        """;

    private readonly ReadOnlySqliteConnectionFactory _factory;

    public ReadOnlySqliteAtlasRepository(ReadOnlySqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task SaveSnapshotAsync(EnvironmentSnapshot snapshot, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            SnapshotHeader? header;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        b.build_id,
                        b.game_assembly_sha256,
                        b.metadata_sha256,
                        b.first_seen_at_utc,
                        b.is_valid,
                        snapshot.atlas_version,
                        snapshot.captured_at_utc,
                        snapshot.snapshot_id,
                        snapshot.identity_version,
                        snapshot.executable_version,
                        snapshot.steam_app_id,
                        snapshot.steam_build_id,
                        snapshot.installation_root,
                        snapshot.game_assembly_path,
                        snapshot.global_metadata_path
                    FROM atlas_state AS state
                    INNER JOIN environment_snapshots AS snapshot
                        ON snapshot.snapshot_id = state.current_snapshot_id
                    INNER JOIN builds AS b
                        ON b.build_id = snapshot.build_id
                    WHERE state.singleton_id = 1;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                header = await reader.ReadAsync() ? ReadSnapshotHeader(reader) : null;
            }

            if (header is null)
            {
                return null;
            }

            var dependencies = await GetDependenciesAsync(connection, header.SnapshotId);
            return new EnvironmentSnapshot(
                header.IdentityVersion,
                header.Build,
                header.Installation,
                dependencies,
                header.AtlasVersion,
                header.CapturedAtUtc);
        }, cancellationToken);

    public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    build_id,
                    game_assembly_sha256,
                    metadata_sha256,
                    first_seen_at_utc,
                    is_valid
                FROM builds
                ORDER BY first_seen_at_utc DESC, build_id DESC;
                """;

            var builds = new List<GameBuild>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builds.Add(ReadBuild(reader));
            }

            return (IReadOnlyList<GameBuild>)builds;
        }, cancellationToken);

    public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT snapshot_id, codebase, channel, source_identity, created_at_utc, environment_snapshot_id
                FROM code_snapshots WHERE snapshot_id = $id;
                """;
            command.Parameters.AddWithValue("$id", snapshotId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadSnapshot(reader) : null;
        }, cancellationToken);

    public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT index_id, snapshot_id, status, started_at_utc, completed_at_utc, failure_message
                FROM index_runs
                WHERE index_id = $id AND status = 'Completed';
                """;
            command.Parameters.AddWithValue("$id", indexId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadRun(reader) : null;
        }, cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
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
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadRun(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSymbol(reader));
            return (IReadOnlyList<IndexSymbolRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolPageAsync(string indexId, int offset, int limit, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSymbol(reader));
            return (IReadOnlyList<IndexSymbolRecord>)result;
        }, cancellationToken);

    public Task<int> CountCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM symbols AS symbol
                INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
                WHERE run.index_id = $id AND run.status = 'Completed';
                """;
            command.Parameters.AddWithValue("$id", indexId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSymbol(reader));
            return (IReadOnlyList<IndexSymbolRecord>)result;
        }, cancellationToken);

    public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
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
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadSymbol(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentNullException.ThrowIfNull(symbolIds);
            var ids = symbolIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) return (IReadOnlyList<IndexSymbolRecord>)Array.Empty<IndexSymbolRecord>();
            const int chunkSize = 500;
            var result = new List<IndexSymbolRecord>(ids.Length);
            for (var offset = 0; offset < ids.Length; offset += chunkSize)
            {
                var chunk = ids.Skip(offset).Take(chunkSize).ToArray();
                await using var command = connection.CreateCommand();
                var parameterNames = new string[chunk.Length];
                for (var index = 0; index < chunk.Length; index++)
                {
                    parameterNames[index] = "$symbol" + index.ToString(CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(parameterNames[index], chunk[index]);
                }
                command.CommandText = $"""
                    SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
                           symbol.qualified_name, symbol.signature, symbol.is_best_effort,
                           symbol.body_recovery_status, symbol.is_public
                    FROM symbols AS symbol
                    INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
                    WHERE run.index_id = $indexId
                      AND run.status = 'Completed'
                      AND symbol.symbol_id IN ({string.Join(", ", parameterNames)});
                    """;
                command.Parameters.AddWithValue("$indexId", indexId);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) result.Add(ReadSymbol(reader));
            }
            return (IReadOnlyList<IndexSymbolRecord>)result.OrderBy(symbol => symbol.SymbolId, StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
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
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "The symbol search limit must be positive.");
            var escaped = EscapeLikePattern(query);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSymbol(reader));
            return (IReadOnlyList<IndexSymbolRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) =>
        GetCompletedRelationshipsByEndpointAsync(indexId, symbolId: null, source: null, cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
        GetCompletedRelationshipsByEndpointAsync(indexId, symbolId, source: true, cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
        GetCompletedRelationshipsByEndpointAsync(indexId, symbolId, source: false, cancellationToken);

    private Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByEndpointAsync(string indexId, string? symbolId, bool? source, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            await using var command = connection.CreateCommand();
            command.CommandText = source is null
                ? """
                  SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                         relationship.target_symbol_id, relationship.target_text, relationship.relationship_kind, relationship.evidence
                  FROM relationships AS relationship
                  INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id
                  WHERE run.index_id = $indexId AND run.status = 'Completed'
                  ORDER BY relationship.relationship_id COLLATE BINARY;
                  """
                : $"""
                   SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                          relationship.target_symbol_id, relationship.target_text, relationship.relationship_kind, relationship.evidence
                   FROM relationships AS relationship
                   INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id
                   WHERE run.index_id = $indexId
                     AND run.status = 'Completed'
                     AND relationship.{(source.Value ? "source_symbol_id" : "target_symbol_id")} = $symbolId
                   ORDER BY relationship.relationship_id COLLATE BINARY;
                   """;
            command.Parameters.AddWithValue("$indexId", indexId);
            if (source is not null)
            {
                command.Parameters.AddWithValue("$symbolId", symbolId);
            }
            var result = new List<IndexRelationshipRecord>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadRelationship(reader));
            return (IReadOnlyList<IndexRelationshipRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSourceFile(reader));
            return (IReadOnlyList<IndexSourceFileRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadSourceLocation(reader));
            return (IReadOnlyList<IndexSourceLocationRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(new IndexFingerprintRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return (IReadOnlyList<IndexFingerprintRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadCallableSurface(reader));
            return (IReadOnlyList<IndexCallableSurfaceRecord>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceByGameSymbolIdAsync(
        string indexId,
        string gameSymbolId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(gameSymbolId);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadCallableSurface(reader));
            return (IReadOnlyList<IndexCallableSurfaceRecord>)result;
        }, cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
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
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadRun(reader) : null;
        }, cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
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
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadRun(reader) : null;
        }, cancellationToken);

    public Task<string?> GetCompletedIndexBuildIdAsync(string indexId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
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
            return (string?)await command.ExecuteScalarAsync();
        }, cancellationToken);

    public Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task CompleteSceneSnapshotAsync(string sceneSnapshotId, SceneWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task PublishSceneSnapshotAsync(string sceneSnapshotId, string publishedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);

    public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            await using var command = connection.CreateCommand();
            command.CommandText = SnapshotSelectSql + " WHERE scene_snapshot_id = $id AND status = 'Completed' AND published_at_utc IS NOT NULL;";
            command.Parameters.AddWithValue("$id", sceneSnapshotId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadSceneSnapshot(reader) : null;
        }, cancellationToken);

    public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
            await using var command = connection.CreateCommand();
            command.CommandText = SnapshotSelectSql + """
                 WHERE build_id = $build AND status = 'Completed' AND published_at_utc IS NOT NULL
                 ORDER BY completed_at_utc DESC, scene_snapshot_id COLLATE BINARY DESC
                 LIMIT 1;
                """;
            command.Parameters.AddWithValue("$build", buildId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadSceneSnapshot(reader) : null;
        }, cancellationToken);

    public Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(string sceneSnapshotId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            await using var counts = connection.CreateCommand();
            counts.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM scene_containers WHERE scene_snapshot_id = snapshot.scene_snapshot_id),
                    (SELECT COUNT(*) FROM scenes WHERE scene_snapshot_id = snapshot.scene_snapshot_id),
                    (SELECT COUNT(*) FROM game_objects WHERE scene_snapshot_id = snapshot.scene_snapshot_id),
                    (SELECT COUNT(*) FROM transforms AS transform
                        INNER JOIN game_objects AS game_object ON game_object.game_object_id = transform.game_object_id
                        WHERE game_object.scene_snapshot_id = snapshot.scene_snapshot_id),
                    (SELECT COUNT(*) FROM components AS component
                        INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                        WHERE game_object.scene_snapshot_id = snapshot.scene_snapshot_id),
                    (SELECT COUNT(*) FROM serialized_refs WHERE scene_snapshot_id = snapshot.scene_snapshot_id)
                FROM scene_snapshots AS snapshot
                WHERE snapshot.scene_snapshot_id = $snapshot
                  AND snapshot.status = 'Completed'
                  AND snapshot.published_at_utc IS NOT NULL;
                """;
            counts.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            int[] values;
            await using (var countReader = await counts.ExecuteReaderAsync())
            {
                if (!await countReader.ReadAsync()) return null;
                values = Enumerable.Range(0, 6).Select(countReader.GetInt32).ToArray();
            }
            await using var recovery = connection.CreateCommand();
            recovery.CommandText = """
                SELECT recovery_status, COUNT(*)
                FROM (
                    SELECT recovery_status FROM scenes WHERE scene_snapshot_id = $snapshot
                    UNION ALL
                    SELECT recovery_status FROM game_objects WHERE scene_snapshot_id = $snapshot
                    UNION ALL
                    SELECT transform.recovery_status FROM transforms AS transform
                        INNER JOIN game_objects AS game_object ON game_object.game_object_id = transform.game_object_id
                        WHERE game_object.scene_snapshot_id = $snapshot
                    UNION ALL
                    SELECT component.recovery_status FROM components AS component
                        INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                        WHERE game_object.scene_snapshot_id = $snapshot
                    UNION ALL
                    SELECT recovery_status FROM serialized_refs WHERE scene_snapshot_id = $snapshot
                )
                GROUP BY recovery_status
                ORDER BY recovery_status COLLATE BINARY;
                """;
            recovery.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            var recoveryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            await using var recoveryReader = await recovery.ExecuteReaderAsync();
            while (await recoveryReader.ReadAsync()) recoveryCounts.Add(recoveryReader.GetString(0), recoveryReader.GetInt32(1));
            return new SceneIndexStatistics(values[0], values[1], values[2], values[3], values[4], values[5], recoveryCounts);
        }, cancellationToken);

    public Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentNullException.ThrowIfNull(containerIds);
            var requested = containerIds.Distinct(StringComparer.Ordinal).ToArray();
            if (requested.Length == 0) return (IReadOnlyList<SceneContainerRecord>)Array.Empty<SceneContainerRecord>();
            await using var command = connection.CreateCommand();
            var parameters = requested.Select((id, index) =>
            {
                var name = "$id" + index;
                command.Parameters.AddWithValue(name, id);
                return name;
            }).ToArray();
            command.CommandText = $"SELECT container.container_id, container.scene_snapshot_id, container.relative_path, container.container_kind, container.unity_version, container.serialized_file_version, container.byte_count, container.sha256, container.sidecar_manifest FROM scene_containers AS container INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = container.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND container.scene_snapshot_id = $snapshot AND container.container_id IN ({string.Join(',', parameters)}) ORDER BY container.container_id COLLATE BINARY;";
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            var rows = new List<SceneContainerRecord>(requested.Length);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadContainer(reader));
            return (IReadOnlyList<SceneContainerRecord>)rows;
        }, cancellationToken);

    public Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
            var total = await CountAsync(connection, """
                SELECT COUNT(*) FROM scenes AS scene
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot
                  AND ($kind IS NULL OR scene.kind = $kind)
                  AND ($query IS NULL OR scene.name LIKE $query ESCAPE '\' COLLATE NOCASE);
                """, ("$snapshot", options.SceneSnapshotId), ("$kind", options.Kind?.ToString()), ("$query", escaped));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT scene.scene_id, scene.scene_snapshot_id, scene.container_id, scene.kind, scene.name, scene.source_local_file_id,
                       scene.object_count, scene.root_count, scene.recovery_status
                FROM scenes AS scene
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot
                  AND ($kind IS NULL OR scene.kind = $kind)
                  AND ($query IS NULL OR scene.name LIKE $query ESCAPE '\' COLLATE NOCASE)
                ORDER BY scene.name COLLATE BINARY, scene.scene_id COLLATE BINARY
                LIMIT $limit;
                """;
            AddPageParameters(command, options.SceneSnapshotId, null, null, options.Kind?.ToString(), escaped, options.Limit);
            var rows = new List<SceneDocumentRecord>(Math.Min(options.Limit, 256));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadDocument(reader));
            return new ScenePageResult<SceneDocumentRecord>(total, rows.Count, rows);
        }, cancellationToken);

    public Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT scene.scene_id, scene.scene_snapshot_id, scene.container_id, scene.kind, scene.name, scene.source_local_file_id, scene.object_count, scene.root_count, scene.recovery_status FROM scenes AS scene INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot AND scene.name = $name COLLATE BINARY AND ($kind IS NULL OR scene.kind = $kind) ORDER BY scene.scene_id COLLATE BINARY LIMIT $limit;";
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$kind", (object?)kind?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<SceneDocumentRecord>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadDocument(reader));
            return (IReadOnlyList<SceneDocumentRecord>)rows;
        }, cancellationToken);

    public Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT scene.scene_id, scene.scene_snapshot_id, scene.container_id, scene.kind, scene.name,
                       scene.source_local_file_id, scene.object_count, scene.root_count, scene.recovery_status
                FROM scenes AS scene INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot AND scene.scene_id = $id;
                """;
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$id", sceneId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadDocument(reader) : null;
        }, cancellationToken);

    public Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
            var total = await CountAsync(connection, """
                SELECT COUNT(*) FROM game_objects AS game_object
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                LEFT JOIN transforms AS transform ON transform.game_object_id = game_object.game_object_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR game_object.scene_id = $scene)
                  AND ($parent IS NULL OR transform.parent_game_object_id = $parent)
                  AND ($query IS NULL OR game_object.name LIKE $query ESCAPE '\' COLLATE NOCASE);
                """, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$parent", options.ParentGameObjectId), ("$query", escaped));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT game_object.game_object_id, game_object.scene_id, game_object.container_id, game_object.local_file_id, game_object.name, game_object.active, game_object.layer, game_object.tag, game_object.recovery_status
                FROM game_objects AS game_object
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                LEFT JOIN transforms AS transform ON transform.game_object_id = game_object.game_object_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR game_object.scene_id = $scene)
                  AND ($parent IS NULL OR transform.parent_game_object_id = $parent)
                  AND ($query IS NULL OR game_object.name LIKE $query ESCAPE '\' COLLATE NOCASE)
                ORDER BY game_object.name COLLATE BINARY, game_object.game_object_id COLLATE BINARY
                LIMIT $limit;
                """;
            AddPageParameters(command, options.SceneSnapshotId, options.SceneId, options.ParentGameObjectId, escaped, options.Limit);
            var rows = new List<SceneGameObjectRecord>(Math.Min(options.Limit, 256));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadGameObject(reader));
            return new ScenePageResult<SceneGameObjectRecord>(total, rows.Count, rows);
        }, cancellationToken);

    public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT game_object_id, scene_id, container_id, local_file_id, name, active, layer, tag, recovery_status FROM game_objects AS item INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = item.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND item.scene_snapshot_id = $snapshot AND item.scene_id = $scene AND item.name = $name COLLATE BINARY ORDER BY item.game_object_id COLLATE BINARY LIMIT $limit;";
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$scene", sceneId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<SceneGameObjectRecord>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadGameObject(reader));
            return (IReadOnlyList<SceneGameObjectRecord>)rows;
        }, cancellationToken);

    public Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentException.ThrowIfNullOrWhiteSpace(gameObjectId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT game_object.game_object_id, game_object.scene_id, game_object.container_id, game_object.local_file_id, game_object.name, game_object.active, game_object.layer, game_object.tag, game_object.recovery_status
                FROM game_objects AS game_object INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot AND game_object.game_object_id = $id;
                """;
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$id", gameObjectId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadGameObject(reader) : null;
        }, cancellationToken);

    public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
            var total = await CountAsync(connection, """
                SELECT COUNT(*) FROM components AS component
                INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR game_object.scene_id = $scene)
                  AND ($gameObject IS NULL OR component.game_object_id = $gameObject)
                  AND ($kind IS NULL OR component.kind = $kind)
                  AND ($query IS NULL OR component.kind LIKE $query ESCAPE '\' COLLATE NOCASE);
                """, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$kind", options.ExactKind), ("$query", escaped));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT component.component_id, component.game_object_id, component.container_id, component.local_file_id,
                       component.unity_class_id, component.kind, component.script_assembly, component.script_namespace,
                       component.script_class, component.resolved_type_symbol_id, component.resolved_code_index_id,
                       component.type_resolution_status, component.recovery_status
                FROM components AS component
                INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR game_object.scene_id = $scene)
                  AND ($gameObject IS NULL OR component.game_object_id = $gameObject)
                  AND ($kind IS NULL OR component.kind = $kind)
                  AND ($query IS NULL OR component.kind LIKE $query ESCAPE '\' COLLATE NOCASE)
                ORDER BY component.kind COLLATE BINARY, component.component_id COLLATE BINARY
                LIMIT $limit;
                """;
            AddPageParameters(command, options.SceneSnapshotId, options.SceneId, options.GameObjectId, options.ExactKind, escaped, options.Limit);
            var rows = new List<SceneComponentRecord>(Math.Min(options.Limit, 256));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadComponent(reader));
            return new ScenePageResult<SceneComponentRecord>(total, rows.Count, rows);
        }, cancellationToken);

    public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT component.component_id, component.game_object_id, component.container_id, component.local_file_id,
                       component.unity_class_id, component.kind, component.script_assembly, component.script_namespace,
                       component.script_class, component.resolved_type_symbol_id, component.resolved_code_index_id,
                       component.type_resolution_status, component.recovery_status
                FROM components AS component
                INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                WHERE snapshot.status = 'Completed'
                  AND snapshot.published_at_utc IS NOT NULL
                  AND game_object.scene_snapshot_id = $snapshot
                  AND (
                        component.kind = $selector COLLATE BINARY
                        OR CASE
                            WHEN component.script_class IS NULL THEN NULL
                            WHEN component.script_namespace IS NULL OR component.script_namespace = '' THEN component.script_class
                            ELSE component.script_namespace || '.' || component.script_class
                           END = $selector COLLATE BINARY)
                ORDER BY component.component_id COLLATE BINARY
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$selector", selector);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<SceneComponentRecord>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadComponent(reader));
            return (IReadOnlyList<SceneComponentRecord>)rows;
        }, cancellationToken);

    public Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
            ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT component.component_id, component.game_object_id, component.container_id, component.local_file_id,
                       component.unity_class_id, component.kind, component.script_assembly, component.script_namespace,
                       component.script_class, component.resolved_type_symbol_id, component.resolved_code_index_id,
                       component.type_resolution_status, component.recovery_status
                FROM components AS component
                INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot AND component.component_id = $id;
                """;
            command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
            command.Parameters.AddWithValue("$id", componentId);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadComponent(reader) : null;
        }, cancellationToken);

    public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
            var total = await CountAsync(connection, """
                SELECT COUNT(*) FROM serialized_refs AS reference
                LEFT JOIN components AS component ON component.component_id = reference.source_component_id
                LEFT JOIN game_objects AS component_game_object ON component_game_object.game_object_id = component.game_object_id
                LEFT JOIN game_objects AS source_game_object
                  ON reference.source_component_id IS NULL
                 AND source_game_object.scene_snapshot_id = reference.scene_snapshot_id
                 AND source_game_object.container_id = reference.source_container_id
                 AND source_game_object.local_file_id = reference.source_local_file_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = reference.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND reference.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR COALESCE(component_game_object.scene_id, source_game_object.scene_id) = $scene)
                  AND ($gameObject IS NULL OR COALESCE(component.game_object_id, source_game_object.game_object_id) = $gameObject)
                  AND ($component IS NULL OR reference.source_component_id = $component)
                  AND ($query IS NULL OR reference.field_path LIKE $query ESCAPE '\' COLLATE NOCASE OR reference.target_text LIKE $query ESCAPE '\' COLLATE NOCASE);
                """, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$component", options.SourceComponentId), ("$query", escaped));
            var unresolved = await CountAsync(connection, """
                SELECT COUNT(*) FROM serialized_refs AS reference
                LEFT JOIN components AS component ON component.component_id = reference.source_component_id
                LEFT JOIN game_objects AS component_game_object ON component_game_object.game_object_id = component.game_object_id
                LEFT JOIN game_objects AS source_game_object
                  ON reference.source_component_id IS NULL
                 AND source_game_object.scene_snapshot_id = reference.scene_snapshot_id
                 AND source_game_object.container_id = reference.source_container_id
                 AND source_game_object.local_file_id = reference.source_local_file_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = reference.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND reference.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR COALESCE(component_game_object.scene_id, source_game_object.scene_id) = $scene)
                  AND ($gameObject IS NULL OR COALESCE(component.game_object_id, source_game_object.game_object_id) = $gameObject)
                  AND ($component IS NULL OR reference.source_component_id = $component)
                  AND ($query IS NULL OR reference.field_path LIKE $query ESCAPE '\' COLLATE NOCASE OR reference.target_text LIKE $query ESCAPE '\' COLLATE NOCASE)
                  AND reference.resolution_status <> 'Resolved';
                """, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$component", options.SourceComponentId), ("$query", escaped));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT reference.reference_id, reference.scene_snapshot_id, reference.source_component_id, reference.field_path, reference.declared_type,
                       reference.source_container_id, reference.source_local_file_id, reference.target_container_id, reference.target_local_file_id,
                       reference.target_game_object_id, reference.target_component_id, reference.target_symbol_id, reference.target_text,
                       reference.resolution_status, reference.evidence, reference.recovery_status
                FROM serialized_refs AS reference
                LEFT JOIN components AS component ON component.component_id = reference.source_component_id
                LEFT JOIN game_objects AS component_game_object ON component_game_object.game_object_id = component.game_object_id
                LEFT JOIN game_objects AS source_game_object
                  ON reference.source_component_id IS NULL
                 AND source_game_object.scene_snapshot_id = reference.scene_snapshot_id
                 AND source_game_object.container_id = reference.source_container_id
                 AND source_game_object.local_file_id = reference.source_local_file_id
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = reference.scene_snapshot_id
                WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND reference.scene_snapshot_id = $snapshot
                  AND ($scene IS NULL OR COALESCE(component_game_object.scene_id, source_game_object.scene_id) = $scene)
                  AND ($gameObject IS NULL OR COALESCE(component.game_object_id, source_game_object.game_object_id) = $gameObject)
                  AND ($component IS NULL OR reference.source_component_id = $component)
                  AND ($query IS NULL OR reference.field_path LIKE $query ESCAPE '\' COLLATE NOCASE OR reference.target_text LIKE $query ESCAPE '\' COLLATE NOCASE)
                ORDER BY reference.field_path COLLATE BINARY, reference.reference_id COLLATE BINARY
                LIMIT $limit;
                """;
            AddReferencePageParameters(command, options.SceneSnapshotId, options.SceneId, options.GameObjectId, options.SourceComponentId, escaped, options.Limit);
            var rows = new List<SceneReferenceRecord>(Math.Min(options.Limit, 256));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(ReadReference(reader));
            return new ScenePageResult<SceneReferenceRecord>(total, rows.Count, rows, unresolved);
        }, cancellationToken);

    public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(string recipeId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {AttemptColumns}
                FROM extraction_attempts
                WHERE recipe_id = $recipeId
                  AND status = $status
                ORDER BY created_at_utc DESC, attempt_id DESC;
                """;
            command.Parameters.AddWithValue("$recipeId", recipeId);
            command.Parameters.AddWithValue("$status", ExtractionAttemptStatus.ProcessCompleted.ToString());
            var attempts = new List<ExtractionAttempt>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) attempts.Add(ReadAttempt(reader));
            return (IReadOnlyList<ExtractionAttempt>)attempts;
        }, cancellationToken);

    public Task<ValidatedExtraction?> GetValidatedExtractionAsync(string extractionId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
            var header = await ReadValidatedExtractionHeaderAsync(connection, extractionId);
            return header is null ? null : await MaterializeValidatedExtractionAsync(connection, header);
        }, cancellationToken);

    public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(string extractionId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    relative_path,
                    kind,
                    size,
                    sha256,
                    assembly_name,
                    module_name,
                    type_count,
                    method_count,
                    field_count,
                    property_count,
                    event_count
                FROM extraction_artifacts
                WHERE extraction_id = $extractionId
                ORDER BY relative_path;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            var artifacts = new List<ArtifactManifestEntry>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) artifacts.Add(ReadArtifactEntry(reader));
            return (IReadOnlyList<ArtifactManifestEntry>)artifacts;
        }, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(string? buildId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            var headers = new List<ValidatedExtraction>();
            await using var command = connection.CreateCommand();
            command.CommandText = buildId is null
                ? $"""
                  SELECT {ValidatedExtractionColumns}
                  FROM validated_extractions
                  ORDER BY created_at_utc DESC, extraction_id DESC;
                  """
                : $"""
                  SELECT {ValidatedExtractionColumns}
                  FROM validated_extractions
                  WHERE build_id = $buildId
                  ORDER BY created_at_utc DESC, extraction_id DESC;
                  """;
            if (buildId is not null) command.Parameters.AddWithValue("$buildId", buildId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) headers.Add(ReadValidatedExtractionHeader(reader));
            return await MaterializeValidatedExtractionsAsync(connection, headers);
        }, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(string recipeId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
            var headers = new List<ValidatedExtraction>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {ValidatedExtractionColumns}
                FROM validated_extractions
                WHERE recipe_id = $recipeId
                ORDER BY created_at_utc DESC, extraction_id DESC;
                """;
            command.Parameters.AddWithValue("$recipeId", recipeId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) headers.Add(ReadValidatedExtractionHeader(reader));
            return await MaterializeValidatedExtractionsAsync(connection, headers);
        }, cancellationToken);

    public Task<StoredValidationResult?> GetLatestValidationResultAsync(string extractionId, string policyDigest, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(policyDigest);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    attempt_id,
                    subject_extraction_id,
                    artifact_manifest_digest,
                    policy_id,
                    policy_version,
                    policy_digest,
                    outcome,
                    report_path,
                    baseline_extraction_id,
                    preference_eligible,
                    validated_at_utc,
                    artifact_count,
                    library_count,
                    managed_assembly_count,
                    type_count,
                    method_count,
                    field_count,
                    property_count,
                    event_count,
                    total_output_bytes,
                    total_managed_bytes
                FROM extraction_validation_results
                WHERE subject_extraction_id = $extractionId
                  AND policy_digest = $policyDigest
                ORDER BY validated_at_utc DESC, attempt_id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            command.Parameters.AddWithValue("$policyDigest", policyDigest);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new StoredValidationResult(
                reader.GetString(0),
                ReadNullableString(reader, 1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                ParseStoredEnum<ValidationOutcome>(reader.GetString(6), "extraction_validation_results.outcome"),
                reader.GetString(7),
                ReadNullableString(reader, 8),
                reader.GetInt64(9) == 1,
                ParseStoredTimestamp(reader.GetString(10), "extraction_validation_results.validated_at_utc"),
                new ExtractionStatistics(reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18), reader.GetInt64(19), reader.GetInt64(20), []));
        }, cancellationToken);

    public Task<PreferredExtraction?> GetPreferredExtractionAsync(string buildId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
            return await ReadPreferredExtractionAsync(connection, buildId);
        }, cancellationToken);

    public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(string? buildId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = buildId is null
                ? $"""
                  SELECT {AttemptColumns}
                  FROM extraction_attempts
                  ORDER BY created_at_utc DESC, attempt_id DESC;
                  """
                : $"""
                  SELECT {AttemptColumns}
                  FROM extraction_attempts
                  WHERE build_id = $buildId
                  ORDER BY created_at_utc DESC, attempt_id DESC;
                  """;
            if (buildId is not null) command.Parameters.AddWithValue("$buildId", buildId);
            var attempts = new List<ExtractionAttempt>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) attempts.Add(ReadAttempt(reader));
            return (IReadOnlyList<ExtractionAttempt>)attempts;
        }, cancellationToken);

    public Task SaveValidationFailureAsync(ValidationPersistence validation, ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task CommitValidatedExtractionAsync(ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task LinkAttemptToValidatedExtractionAsync(ValidationPersistence validation, ValidatedExtraction extraction, ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task SaveRevalidationAsync(ValidationPersistence validation, ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task SetPreferredExtractionAsync(PreferredExtraction preference, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task ClearPreferredExtractionAsync(string buildId, string expectedExtractionId, ExtractionPreferenceReason reason, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task DeleteCleanupEligibleAttemptAsync(string attemptId, ExtractionAttemptStatus expectedStatus, DateTimeOffset expectedCompletedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadOnlyMessage);

    private Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_factory.Open());
    }

    private async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await action(connection);
    }

    private static async Task<IReadOnlyList<DependencyVersion>> GetDependenciesAsync(SqliteConnection connection, string snapshotId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, version, path, is_installed
            FROM dependencies
            WHERE snapshot_id = $snapshotId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        var dependencies = new List<DependencyVersion>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dependencies.Add(new DependencyVersion(
                Enum.Parse<DependencyKind>(reader.GetString(0), ignoreCase: false),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) == 1));
        }

        return dependencies;
    }

    private static SnapshotHeader ReadSnapshotHeader(SqliteDataReader reader)
    {
        var build = ReadBuild(reader);
        var installation = new InstallationObservation(
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
        return new SnapshotHeader(build, reader.GetString(5), ParseTimestamp(reader.GetString(6)), reader.GetString(7), reader.GetInt32(8), installation);
    }

    private static GameBuild ReadBuild(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTimestamp(reader.GetString(3)), reader.GetInt64(4) == 1);

    private static CodeSnapshotRecord ReadSnapshot(SqliteDataReader reader) =>
        new(reader.GetString(0), Enum.Parse<CodebaseKind>(reader.GetString(1)), Enum.Parse<CodeChannel>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static IndexRunRecord ReadRun(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), Enum.Parse<IndexRunStatus>(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static IndexSymbolRecord ReadSymbol(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6) != 0, reader.IsDBNull(7) ? null : Enum.Parse<BodyRecoveryStatus>(reader.GetString(7)), reader.GetInt64(8) != 0);

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

    private static IndexRelationshipRecord ReadRelationship(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6));

    private static IndexSourceFileRecord ReadSourceFile(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));

    private static IndexSourceLocationRecord ReadSourceLocation(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5));

    private static SceneSnapshotRecord ReadSceneSnapshot(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), Enum.Parse<SceneSnapshotStatus>(reader.GetString(9)), Enum.Parse<SceneRecoveryStatus>(reader.GetString(10)), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14));

    private static SceneContainerRecord ReadContainer(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8));

    private static SceneDocumentRecord ReadDocument(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Enum.Parse<SceneDocumentKind>(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7), Enum.Parse<SceneRecoveryStatus>(reader.GetString(8)));

    private static SceneGameObjectRecord ReadGameObject(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), Enum.Parse<SceneRecoveryStatus>(reader.GetString(8)));

    private static SceneComponentRecord ReadComponent(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), Enum.Parse<SceneResolutionStatus>(reader.GetString(11)), Enum.Parse<SceneRecoveryStatus>(reader.GetString(12)));

    private static SceneReferenceRecord ReadReference(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), Enum.Parse<SceneResolutionStatus>(reader.GetString(13)), reader.GetString(14), Enum.Parse<SceneRecoveryStatus>(reader.GetString(15)));

    private static ExtractionAttempt ReadAttempt(SqliteDataReader reader) =>
        new(
            AttemptId: reader.GetString(0),
            RecipeId: ReadNullableString(reader, 1),
            BuildId: reader.GetString(2),
            ToolInstanceId: ReadNullableString(reader, 3),
            ProfileId: reader.GetString(4),
            ProfileVersion: reader.GetInt32(5),
            ProfileDigest: reader.GetString(6),
            ValidationPolicyId: reader.GetString(7),
            ValidationPolicyVersion: reader.GetInt32(8),
            ValidationPolicyDigest: reader.GetString(9),
            AdapterVersion: reader.GetInt32(10),
            ExtractionSchemaVersion: reader.GetInt32(11),
            InputSource: reader.IsDBNull(12)
                ? null
                : ParseStoredEnum<ExtractionInputSource>(
                    reader.GetString(12),
                    "extraction_attempts.input_source"),
            InputSnapshotId: ReadNullableString(reader, 13),
            Status: ParseStoredEnum<ExtractionAttemptStatus>(
                reader.GetString(14),
                "extraction_attempts.status"),
            CreatedAtUtc: ParseStoredTimestamp(
                reader.GetString(15),
                "extraction_attempts.created_at_utc"),
            StartedAtUtc: ReadNullableTimestamp(
                reader,
                16,
                "extraction_attempts.started_at_utc"),
            CompletedAtUtc: ReadNullableTimestamp(
                reader,
                17,
                "extraction_attempts.completed_at_utc"),
            PreInputManifestDigest: ReadNullableString(reader, 18),
            PostInputManifestDigest: ReadNullableString(reader, 19),
            WorkingPath: reader.GetString(20),
            StandardOutputPath: reader.GetString(21),
            StandardErrorPath: reader.GetString(22),
            StandardOutputTruncated: reader.GetInt64(23) == 1,
            StandardErrorTruncated: reader.GetInt64(24) == 1,
            StandardOutputDiscardedBytes: reader.GetInt64(25),
            StandardErrorDiscardedBytes: reader.GetInt64(26),
            ProcessId: reader.IsDBNull(27) ? null : reader.GetInt32(27),
            ProcessExitCode: reader.IsDBNull(28) ? null : reader.GetInt32(28),
            FailureStage: reader.IsDBNull(29)
                ? null
                : ParseStoredEnum<ExtractionFailureStage>(
                    reader.GetString(29),
                    "extraction_attempts.failure_stage"),
            FailureCode: reader.IsDBNull(30)
                ? null
                : ParseStoredEnum<ExtractionFailureCode>(
                    reader.GetString(30),
                    "extraction_attempts.failure_code"),
            FailureMessage: ReadNullableString(reader, 31),
            KeepFailedArtifacts: reader.GetInt64(32) == 1,
            DiscardedFileCount: reader.GetInt32(33),
            DiscardedByteCount: reader.GetInt64(34),
            CandidateOutputPath: ReadNullableString(reader, 35),
            ResultExtractionId: ReadNullableString(reader, 36),
            ValidationSourceExtractionId: ReadNullableString(reader, 37));

    private static ArtifactManifestEntry ReadArtifactEntry(SqliteDataReader reader) =>
        new(reader.GetString(0), ParseStoredEnum<ArtifactKind>(reader.GetString(1), "extraction_artifacts.kind"), reader.GetInt64(2), reader.GetString(3), ReadNullableString(reader, 4), ReadNullableString(reader, 5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10));

    private static ValidatedExtraction ReadValidatedExtractionHeader(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetString(10), reader.GetString(11), ParseStoredTimestamp(reader.GetString(12), "validated_extractions.created_at_utc"), ParseStoredEnum<ToolTrustLevel>(reader.GetString(13), "validated_extractions.trust_level"), ParseStoredEnum<ValidationOutcome>(reader.GetString(14), "validated_extractions.validation_outcome"), new ExtractionStatistics(reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18), reader.GetInt32(19), reader.GetInt32(20), reader.GetInt32(21), reader.GetInt32(22), reader.GetInt64(23), reader.GetInt64(24), []));

    private static async Task<IReadOnlyList<AssemblyIdentityStatistics>> ReadAssemblyStatisticsAsync(SqliteConnection connection, string extractionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                assembly_name,
                COUNT(*),
                SUM(size),
                SUM(COALESCE(type_count, 0)),
                SUM(COALESCE(method_count, 0)),
                SUM(COALESCE(field_count, 0)),
                SUM(COALESCE(property_count, 0)),
                SUM(COALESCE(event_count, 0))
            FROM extraction_artifacts
            WHERE extraction_id = $extractionId
              AND assembly_name IS NOT NULL
            GROUP BY assembly_name
            ORDER BY assembly_name COLLATE NOCASE, assembly_name;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        var assemblies = new List<AssemblyIdentityStatistics>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            assemblies.Add(new AssemblyIdentityStatistics(reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetInt64(2), checked((int)reader.GetInt64(3)), checked((int)reader.GetInt64(4)), checked((int)reader.GetInt64(5)), checked((int)reader.GetInt64(6)), checked((int)reader.GetInt64(7))));
        }
        return assemblies;
    }

    private static async Task<IReadOnlyList<ValidatedExtraction>> MaterializeValidatedExtractionsAsync(SqliteConnection connection, IReadOnlyList<ValidatedExtraction> headers)
    {
        var materialized = new List<ValidatedExtraction>(headers.Count);
        foreach (var header in headers)
        {
            materialized.Add(await MaterializeValidatedExtractionAsync(connection, header));
        }
        return materialized;
    }

    private static async Task<ValidatedExtraction> MaterializeValidatedExtractionAsync(SqliteConnection connection, ValidatedExtraction header)
    {
        var assemblies = await ReadAssemblyStatisticsAsync(connection, header.ExtractionId);
        return header with { Statistics = header.Statistics with { Assemblies = assemblies } };
    }

    private static async Task<ValidatedExtraction?> ReadValidatedExtractionHeaderAsync(SqliteConnection connection, string extractionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ValidatedExtractionColumns}
            FROM validated_extractions
            WHERE extraction_id = $extractionId;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadValidatedExtractionHeader(reader) : null;
    }

    private static async Task<PreferredExtraction?> ReadPreferredExtractionAsync(SqliteConnection connection, string buildId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT build_id, extraction_id, selected_at_utc, selection_reason
            FROM preferred_extractions
            WHERE build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new PreferredExtraction(reader.GetString(0), reader.GetString(1), ParseStoredTimestamp(reader.GetString(2), "preferred_extractions.selected_at_utc"), ParseStoredEnum<ExtractionPreferenceReason>(reader.GetString(3), "preferred_extractions.selection_reason"))
            : null;
    }

    private static async Task<IReadOnlyList<DependencyVersion>> GetDependenciesAsync(SqliteConnection connection, string snapshotId, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, version, path, is_installed
            FROM dependencies
            WHERE snapshot_id = $snapshotId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        var dependencies = new List<DependencyVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dependencies.Add(new DependencyVersion(Enum.Parse<DependencyKind>(reader.GetString(0), ignoreCase: false), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3) == 1));
        }
        return dependencies;
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static void AddPageParameters(SqliteCommand command, string snapshot, string? scene, string? parent, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static void AddPageParameters(SqliteCommand command, string snapshot, string? scene, string? gameObject, string? kind, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameObject", (object?)gameObject ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static void AddReferencePageParameters(SqliteCommand command, string snapshot, string? scene, string? gameObject, string? component, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameObject", (object?)gameObject ?? DBNull.Value);
        command.Parameters.AddWithValue("$component", (object?)component ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string EscapeSceneLikePattern(string value) => EscapeLikePattern(value);

    private static TEnum ParseStoredEnum<TEnum>(string value, string fieldName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException($"Stored field '{fieldName}' contains unknown enum value '{value}'.");
        }
        return parsed;
    }

    private static DateTimeOffset ParseStoredTimestamp(string value, string fieldName)
    {
        try { return ParseTimestamp(value); }
        catch (FormatException exception) { throw new InvalidOperationException($"Stored field '{fieldName}' contains an invalid timestamp.", exception); }
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal, string fieldName) =>
        reader.IsDBNull(ordinal) ? null : ParseStoredTimestamp(reader.GetString(ordinal), fieldName);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);

    private sealed record SnapshotHeader(GameBuild Build, string AtlasVersion, DateTimeOffset CapturedAtUtc, string SnapshotId, int IdentityVersion, InstallationObservation Installation);
}
