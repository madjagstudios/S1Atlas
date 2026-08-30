using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    public async Task SaveNativeRecoveryAsync(
        NativeRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        var canonicalRecord = NativeRecoverySqlite.NormalizeRecord(record);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await RequireCompletedNativeInputAsync(
                connection,
                transaction,
                canonicalRecord.Request,
                cancellationToken);
            await InsertNativeRunAsync(connection, transaction, canonicalRecord, cancellationToken);
            await InsertNativeEdgesAsync(connection, transaction, canonicalRecord, cancellationToken);
            await InsertNativeFieldsAsync(connection, transaction, canonicalRecord, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<NativeRecoveryRecord?> GetNativeRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await NativeRecoverySqlite.GetByIdAsync(
            connection,
            recoveryId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<NativeRecoveryRecord>> GetNativeRecoveriesAsync(
        NativeRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await NativeRecoverySqlite.GetMatchingAsync(
            connection,
            request,
            cancellationToken);
    }

    private static async Task RequireCompletedNativeInputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM index_runs AS run
            INNER JOIN code_snapshots AS snapshot
                ON snapshot.snapshot_id = run.snapshot_id
            INNER JOIN environment_snapshots AS environment
                ON environment.snapshot_id = snapshot.environment_snapshot_id
            INNER JOIN builds AS build
                ON build.build_id = environment.build_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND snapshot.codebase = 'ScheduleI'
              AND snapshot.channel = 'Installed'
              AND build.build_id = $buildId
              AND build.game_assembly_sha256 = $gameAssemblySha256;
            """;
        command.Parameters.AddWithValue("$indexId", request.IndexId);
        command.Parameters.AddWithValue("$buildId", request.BuildId);
        command.Parameters.AddWithValue("$gameAssemblySha256", request.GameAssemblySha256);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
                "Native recovery records require a completed Schedule I index matching the recorded build and GameAssembly hash.");
        }
    }

    private static async Task InsertNativeRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO native_recovery_runs (
                recovery_id, build_id, index_id, game_assembly_sha256,
                symbol_ids_json, max_traversal_edges, tool_name, tool_version,
                tool_sha256, status, mapping_evidence_json, is_complete,
                output_sha256, created_at_utc, failure_message)
            VALUES (
                $recoveryId, $buildId, $indexId, $gameAssemblySha256,
                $symbolIdsJson, $maxTraversalEdges, $toolName, $toolVersion,
                $toolSha256, $status, $mappingEvidenceJson, $isComplete,
                $outputSha256, $createdAtUtc, $failureMessage);
            """;
        command.Parameters.AddWithValue("$recoveryId", record.RecoveryId);
        command.Parameters.AddWithValue("$buildId", record.Request.BuildId);
        command.Parameters.AddWithValue("$indexId", record.Request.IndexId);
        command.Parameters.AddWithValue("$gameAssemblySha256", record.Request.GameAssemblySha256);
        command.Parameters.AddWithValue("$symbolIdsJson", NativeRecoverySqlite.SerializeStrings(record.Request.SymbolIds));
        command.Parameters.AddWithValue("$maxTraversalEdges", record.Request.MaxTraversalEdges);
        command.Parameters.AddWithValue("$toolName", record.ToolName);
        command.Parameters.AddWithValue("$toolVersion", record.ToolVersion);
        command.Parameters.AddWithValue("$toolSha256", record.ToolSha256);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$mappingEvidenceJson", NativeRecoverySqlite.SerializeStrings(record.MappingEvidence));
        command.Parameters.AddWithValue("$isComplete", record.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$outputSha256", record.OutputSha256);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$failureMessage", (object?)record.FailureMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertNativeEdgesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < record.Edges.Count; ordinal++)
        {
            var edge = record.Edges[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO native_recovery_edges (
                    recovery_id, ordinal, edge_id, source_method_pointer,
                    target_method_pointer, target_text, kind, evidence, is_complete)
                VALUES (
                    $recoveryId, $ordinal, $edgeId, $sourceMethodPointer,
                    $targetMethodPointer, $targetText, $kind, $evidence, $isComplete);
                """;
            command.Parameters.AddWithValue("$recoveryId", record.RecoveryId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$edgeId", edge.EdgeId);
            command.Parameters.AddWithValue("$sourceMethodPointer", edge.SourceMethodPointer);
            command.Parameters.AddWithValue("$targetMethodPointer", (object?)edge.TargetMethodPointer ?? DBNull.Value);
            command.Parameters.AddWithValue("$targetText", (object?)edge.TargetText ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", edge.Kind);
            command.Parameters.AddWithValue("$evidence", edge.Evidence);
            command.Parameters.AddWithValue("$isComplete", edge.IsComplete ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertNativeFieldsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < record.FieldAccesses.Count; ordinal++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO native_recovery_fields (recovery_id, ordinal, field_access)
                VALUES ($recoveryId, $ordinal, $fieldAccess);
                """;
            command.Parameters.AddWithValue("$recoveryId", record.RecoveryId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$fieldAccess", record.FieldAccesses[ordinal]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
