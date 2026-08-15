using Microsoft.Data.Sqlite;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository : ISceneRepository
{
    public async Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SceneSnapshotStatus.Running)
            throw new ArgumentException("A scene snapshot must be created in Running status.", nameof(snapshot));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT status, published_at_utc FROM scene_snapshots WHERE scene_snapshot_id = $id;";
                existing.Parameters.AddWithValue("$id", snapshot.SceneSnapshotId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var status = reader.GetString(0);
                    if (!reader.IsDBNull(1))
                        throw new InvalidOperationException($"Published scene snapshot '{snapshot.SceneSnapshotId}' is immutable.");
                    if (string.Equals(status, "Running", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Scene snapshot '{snapshot.SceneSnapshotId}' is already being indexed.");
                }
            }

            await using (var reconcile = connection.CreateCommand())
            {
                reconcile.Transaction = transaction;
                reconcile.CommandText = """
                    DELETE FROM serialized_refs WHERE scene_snapshot_id = $id;
                    DELETE FROM transforms
                    WHERE game_object_id IN (
                        SELECT game_object_id FROM game_objects WHERE scene_snapshot_id = $id);
                    DELETE FROM components
                    WHERE game_object_id IN (
                        SELECT game_object_id FROM game_objects WHERE scene_snapshot_id = $id);
                    DELETE FROM game_objects WHERE scene_snapshot_id = $id;
                    DELETE FROM scenes WHERE scene_snapshot_id = $id;
                    DELETE FROM scene_containers WHERE scene_snapshot_id = $id;
                    DELETE FROM scene_snapshots
                    WHERE scene_snapshot_id = $id
                      AND status IN ('Completed', 'Failed')
                      AND published_at_utc IS NULL;
                    """;
                reconcile.Parameters.AddWithValue("$id", snapshot.SceneSnapshotId);
                await reconcile.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO scene_snapshots (
                    scene_snapshot_id, build_id, extraction_id, input_snapshot_id,
                    code_snapshot_id, code_index_id, parser_id, parser_version,
                    container_manifest_digest, status, recovery_status, started_at_utc,
                    completed_at_utc, failure_code, failure_message)
                VALUES (
                    $id, $build, $extraction, $input, $codeSnapshot, $codeIndex,
                    $parserId, $parserVersion, $digest, 'Running', $recovery, $started,
                    NULL, NULL, NULL);
                """;
            AddSnapshotParameters(command, snapshot);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(startedAtUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scene_snapshots
            SET started_at_utc = $started
            WHERE scene_snapshot_id = $id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$id", sceneSnapshotId);
        command.Parameters.AddWithValue("$started", startedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Scene snapshot '{sceneSnapshotId}' is not running.");
    }

    public async Task CompleteSceneSnapshotAsync(
        string sceneSnapshotId,
        SceneWriteSet writeSet,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentNullException.ThrowIfNull(writeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var snapshot = await GetRunningSceneSnapshotAsync(connection, transaction, sceneSnapshotId, cancellationToken)
                ?? throw new InvalidOperationException($"Scene snapshot '{sceneSnapshotId}' is not running.");

            ValidateWriteSetOwnership(sceneSnapshotId, writeSet);
            await ValidateSameBuildAuthoritiesAsync(connection, transaction, sceneSnapshotId, cancellationToken);
            await ValidateResolvedComponentAuthoritiesAsync(
                connection,
                transaction,
                snapshot,
                writeSet.Components,
                cancellationToken);
            await ValidateReferenceSymbolAuthoritiesAsync(
                connection,
                transaction,
                snapshot,
                writeSet.References,
                cancellationToken);

            foreach (var container in writeSet.Containers)
                await InsertContainerAsync(connection, transaction, container, cancellationToken);
            foreach (var document in writeSet.Documents)
                await InsertDocumentAsync(connection, transaction, document, cancellationToken);
            foreach (var gameObject in writeSet.GameObjects)
                await InsertGameObjectAsync(connection, transaction, sceneSnapshotId, gameObject, cancellationToken);
            foreach (var transform in writeSet.Transforms)
                await InsertTransformAsync(connection, transaction, transform, cancellationToken);
            foreach (var component in writeSet.Components)
                await InsertComponentAsync(connection, transaction, component, cancellationToken);
            foreach (var reference in writeSet.References)
                await InsertReferenceAsync(connection, transaction, reference, cancellationToken);

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE scene_snapshots
                SET status = 'Completed', completed_at_utc = $completed,
                    recovery_status = $recovery,
                    failure_code = NULL, failure_message = NULL
                WHERE scene_snapshot_id = $id AND status = 'Running';
                """;
            update.Parameters.AddWithValue("$completed", completedAtUtc);
            update.Parameters.AddWithValue("$recovery", writeSet.Snapshot.RecoveryStatus.ToString());
            update.Parameters.AddWithValue("$id", snapshot.SceneSnapshotId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Scene snapshot '{sceneSnapshotId}' could not be completed.");

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task FailSceneSnapshotAsync(
        string sceneSnapshotId,
        string failureCode,
        string failureMessage,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scene_snapshots
            SET status = 'Failed', completed_at_utc = $completed,
                failure_code = $code, failure_message = $message
            WHERE scene_snapshot_id = $id
              AND (status = 'Running' OR (status = 'Completed' AND published_at_utc IS NULL));
            """;
        command.Parameters.AddWithValue("$id", sceneSnapshotId);
        command.Parameters.AddWithValue("$code", failureCode);
        command.Parameters.AddWithValue("$message", failureMessage);
        command.Parameters.AddWithValue("$completed", completedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Scene snapshot '{sceneSnapshotId}' could not be failed.");
    }

    public async Task PublishSceneSnapshotAsync(
        string sceneSnapshotId,
        string publishedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedAtUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scene_snapshots
            SET published_at_utc = $published
            WHERE scene_snapshot_id = $id
              AND status = 'Completed'
              AND published_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", sceneSnapshotId);
        command.Parameters.AddWithValue("$published", publishedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Scene snapshot '{sceneSnapshotId}' could not be published.");
    }

    public async Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SnapshotSelectSql + " WHERE scene_snapshot_id = $id AND status = 'Completed' AND published_at_utc IS NOT NULL;";
        command.Parameters.AddWithValue("$id", sceneSnapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSceneSnapshot(reader) : null;
    }

    public async Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SnapshotSelectSql + """
             WHERE build_id = $build AND status = 'Completed' AND published_at_utc IS NOT NULL
             ORDER BY completed_at_utc DESC, scene_snapshot_id COLLATE BINARY DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$build", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSceneSnapshot(reader) : null;
    }

    public async Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(
        string sceneSnapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using (var countReader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            if (!await countReader.ReadAsync(cancellationToken))
                return null;
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
        await using var recoveryReader = await recovery.ExecuteReaderAsync(cancellationToken);
        while (await recoveryReader.ReadAsync(cancellationToken))
            recoveryCounts.Add(recoveryReader.GetString(0), recoveryReader.GetInt32(1));

        return new SceneIndexStatistics(
            values[0], values[1], values[2], values[3], values[4], values[5], recoveryCounts);
    }

    public async Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId); ArgumentNullException.ThrowIfNull(containerIds);
        var requested = containerIds.Distinct(StringComparer.Ordinal).ToArray(); if (requested.Length == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken); await using var command = connection.CreateCommand();
        var parameters = requested.Select((id, index) => { var name = "$id" + index; command.Parameters.AddWithValue(name, id); return name; }).ToArray();
        command.CommandText = $"SELECT container.container_id, container.scene_snapshot_id, container.relative_path, container.container_kind, container.unity_version, container.serialized_file_version, container.byte_count, container.sha256, container.sidecar_manifest FROM scene_containers AS container INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = container.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND container.scene_snapshot_id = $snapshot AND container.container_id IN ({string.Join(',', parameters)}) ORDER BY container.container_id COLLATE BINARY;";
        command.Parameters.AddWithValue("$snapshot", sceneSnapshotId); var rows = new List<SceneContainerRecord>(requested.Length); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadContainer(reader)); return rows;
    }

    public async Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var total = await CountAsync(connection, """
            SELECT COUNT(*) FROM scenes AS scene
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id
            WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot
              AND ($kind IS NULL OR scene.kind = $kind)
              AND ($query IS NULL OR scene.name LIKE $query ESCAPE '\' COLLATE NOCASE);
            """, cancellationToken, ("$snapshot", options.SceneSnapshotId), ("$kind", options.Kind?.ToString()), ("$query", escaped));
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
        AddPageParameters(command, options.SceneSnapshotId, options.Kind?.ToString(), escaped, options.Limit);
        var rows = new List<SceneDocumentRecord>(Math.Min(options.Limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadDocument(reader));
        return new ScenePageResult<SceneDocumentRecord>(total, rows.Count, rows);
    }

    public async Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scene.scene_id, scene.scene_snapshot_id, scene.container_id, scene.kind, scene.name,
                   scene.source_local_file_id, scene.object_count, scene.root_count, scene.recovery_status
            FROM scenes AS scene INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id
            WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot AND scene.scene_id = $id;
            """;
        command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
        command.Parameters.AddWithValue("$id", sceneId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    public async Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await OpenConnectionAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT scene.scene_id, scene.scene_snapshot_id, scene.container_id, scene.kind, scene.name, scene.source_local_file_id, scene.object_count, scene.root_count, scene.recovery_status FROM scenes AS scene INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = scene.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND scene.scene_snapshot_id = $snapshot AND scene.name = $name COLLATE BINARY AND ($kind IS NULL OR scene.kind = $kind) ORDER BY scene.scene_id COLLATE BINARY LIMIT $limit;";
        command.Parameters.AddWithValue("$snapshot", sceneSnapshotId); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$kind", (object?)kind?.ToString() ?? DBNull.Value); command.Parameters.AddWithValue("$limit", limit); var rows = new List<SceneDocumentRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadDocument(reader)); return rows;
    }

    public async Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var total = await CountAsync(connection, """
            SELECT COUNT(*) FROM game_objects AS game_object
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            LEFT JOIN transforms AS transform ON transform.game_object_id = game_object.game_object_id
            WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
              AND ($scene IS NULL OR game_object.scene_id = $scene)
              AND ($parent IS NULL OR transform.parent_game_object_id = $parent)
              AND ($query IS NULL OR game_object.name LIKE $query ESCAPE '\' COLLATE NOCASE);
            """, cancellationToken, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$parent", options.ParentGameObjectId), ("$query", escaped));
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadGameObject(reader));
        return new ScenePageResult<SceneGameObjectRecord>(total, rows.Count, rows);
    }

    public async Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameObjectId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_object.game_object_id, game_object.scene_id, game_object.container_id, game_object.local_file_id, game_object.name, game_object.active, game_object.layer, game_object.tag, game_object.recovery_status
            FROM game_objects AS game_object INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot AND game_object.game_object_id = $id;
            """;
        command.Parameters.AddWithValue("$snapshot", sceneSnapshotId);
        command.Parameters.AddWithValue("$id", gameObjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGameObject(reader) : null;
    }

    public async Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await OpenConnectionAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT game_object_id, scene_id, container_id, local_file_id, name, active, layer, tag, recovery_status FROM game_objects AS item INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = item.scene_snapshot_id WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND item.scene_snapshot_id = $snapshot AND item.scene_id = $scene AND item.name = $name COLLATE BINARY ORDER BY item.game_object_id COLLATE BINARY LIMIT $limit;";
        command.Parameters.AddWithValue("$snapshot", sceneSnapshotId); command.Parameters.AddWithValue("$scene", sceneId); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$limit", limit); var rows = new List<SceneGameObjectRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadGameObject(reader)); return rows;
    }

    public async Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var total = await CountAsync(connection, """
            SELECT COUNT(*) FROM components AS component
            INNER JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL AND game_object.scene_snapshot_id = $snapshot
              AND ($scene IS NULL OR game_object.scene_id = $scene)
              AND ($gameObject IS NULL OR component.game_object_id = $gameObject)
              AND ($kind IS NULL OR component.kind = $kind)
              AND ($query IS NULL OR component.kind LIKE $query ESCAPE '\' COLLATE NOCASE);
            """, cancellationToken, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$kind", options.ExactKind), ("$query", escaped));
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadComponent(reader));
        return new ScenePageResult<SceneComponentRecord>(total, rows.Count, rows);
    }

    public async Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadComponent(reader) : null;
    }

    public async Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken); await using var command = connection.CreateCommand();
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadComponent(reader));
        return rows;
    }

    public async Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var escaped = options.Query is null ? null : "%" + EscapeSceneLikePattern(options.Query) + "%";
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
            """, cancellationToken, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$component", options.SourceComponentId), ("$query", escaped));
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
            """, cancellationToken, ("$snapshot", options.SceneSnapshotId), ("$scene", options.SceneId), ("$gameObject", options.GameObjectId), ("$component", options.SourceComponentId), ("$query", escaped));
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadReference(reader));
        return new ScenePageResult<SceneReferenceRecord>(total, rows.Count, rows, unresolved);
    }

    private const string SnapshotSelectSql = """
        SELECT scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id,
               code_index_id, parser_id, parser_version, container_manifest_digest, status,
               recovery_status, started_at_utc, completed_at_utc, failure_code, failure_message
        FROM scene_snapshots
        """;

    private static void ValidateWriteSetOwnership(string sceneSnapshotId, SceneWriteSet writeSet)
    {
        if (!string.Equals(writeSet.Snapshot.SceneSnapshotId, sceneSnapshotId, StringComparison.Ordinal) ||
            writeSet.Containers.Any(row => !string.Equals(row.SceneSnapshotId, sceneSnapshotId, StringComparison.Ordinal)) ||
            writeSet.Documents.Any(row => !string.Equals(row.SceneSnapshotId, sceneSnapshotId, StringComparison.Ordinal)) ||
            writeSet.References.Any(row => !string.Equals(row.SceneSnapshotId, sceneSnapshotId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Scene write-set rows must belong to the running snapshot.");

        var containers = writeSet.Containers.Select(row => row.ContainerId).ToHashSet(StringComparer.Ordinal);
        var documents = writeSet.Documents.Select(row => row.SceneId).ToHashSet(StringComparer.Ordinal);
        var gameObjects = writeSet.GameObjects.Select(row => row.GameObjectId).ToHashSet(StringComparer.Ordinal);
        var components = writeSet.Components.Select(row => row.ComponentId).ToHashSet(StringComparer.Ordinal);
        if (writeSet.Documents.Any(row => !containers.Contains(row.ContainerId)) ||
            writeSet.GameObjects.Any(row => !documents.Contains(row.SceneId) || !containers.Contains(row.ContainerId)) ||
            writeSet.Transforms.Any(row => !gameObjects.Contains(row.GameObjectId) || (row.ParentGameObjectId is not null && !gameObjects.Contains(row.ParentGameObjectId))) ||
            writeSet.Components.Any(row => !gameObjects.Contains(row.GameObjectId) || !containers.Contains(row.ContainerId)) ||
            writeSet.References.Any(row => !containers.Contains(row.SourceContainerId) ||
                (row.SourceComponentId is not null && !components.Contains(row.SourceComponentId)) ||
                (row.TargetContainerId is not null && !containers.Contains(row.TargetContainerId)) ||
                (row.TargetGameObjectId is not null && !gameObjects.Contains(row.TargetGameObjectId)) ||
                (row.TargetComponentId is not null && !components.Contains(row.TargetComponentId))))
            throw new InvalidOperationException("Scene write-set child rows must belong to parents in the running snapshot.");
    }

    private static async Task ValidateSameBuildAuthoritiesAsync(SqliteConnection connection, SqliteTransaction transaction, string sceneSnapshotId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN scene.build_id = extraction.build_id
                              AND scene.build_id = input.build_id
                              AND scene.build_id = environment.build_id
                              AND input.replay_verified = 1
                              AND extraction_attempt.input_snapshot_id = scene.input_snapshot_id
                              AND code_snapshot.codebase = 'ScheduleI'
                              AND code_snapshot.channel = 'Installed'
                              AND index_run.snapshot_id = scene.code_snapshot_id
                              AND index_run.status = 'Completed'
                         THEN 1 ELSE 0 END
            FROM scene_snapshots AS scene
            INNER JOIN validated_extractions AS extraction ON extraction.extraction_id = scene.extraction_id
            INNER JOIN extraction_attempts AS extraction_attempt ON extraction_attempt.attempt_id = extraction.source_attempt_id
            INNER JOIN input_snapshots AS input ON input.input_snapshot_id = scene.input_snapshot_id
            INNER JOIN code_snapshots AS code_snapshot ON code_snapshot.snapshot_id = scene.code_snapshot_id
            INNER JOIN environment_snapshots AS environment ON environment.snapshot_id = code_snapshot.environment_snapshot_id
            INNER JOIN index_runs AS index_run ON index_run.index_id = scene.code_index_id
            WHERE scene.scene_snapshot_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sceneSnapshotId);
        var valid = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!valid)
            throw new InvalidOperationException("Scene snapshot authorities must use the replay-verified input of the same-build validated extraction and completed Schedule I index.");
    }

    private static async Task ValidateResolvedComponentAuthoritiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SceneSnapshotRecord snapshot,
        IReadOnlyList<SceneComponentRecord> components,
        CancellationToken cancellationToken)
    {
        foreach (var component in components.Where(component => component.TypeResolutionStatus == SceneResolutionStatus.Resolved))
        {
            if (component.ResolvedTypeSymbolId is null || component.ResolvedCodeIndexId is null ||
                !string.Equals(component.ResolvedCodeIndexId, snapshot.CodeIndexId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resolved scene components must identify the snapshot's completed code index and a type symbol.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM symbols AS symbol
                INNER JOIN code_snapshots AS code_snapshot ON code_snapshot.snapshot_id = symbol.snapshot_id
                WHERE symbol.symbol_id = $symbol
                  AND symbol.snapshot_id = $codeSnapshot
                  AND symbol.kind = 'Type'
                  AND symbol.canonical_key LIKE 'ScheduleI:Installed:Type:%'
                  AND code_snapshot.codebase = 'ScheduleI'
                  AND code_snapshot.channel = 'Installed';
                """;
            command.Parameters.AddWithValue("$symbol", component.ResolvedTypeSymbolId);
            command.Parameters.AddWithValue("$codeSnapshot", snapshot.CodeSnapshotId);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("Resolved scene component type symbols must belong to the snapshot's Schedule I code snapshot.");
            }
        }
    }

    private static async Task ValidateReferenceSymbolAuthoritiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SceneSnapshotRecord snapshot,
        IReadOnlyList<SceneReferenceRecord> references,
        CancellationToken cancellationToken)
    {
        var symbolIds = references
            .Select(reference => reference.TargetSymbolId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var symbolChunk in symbolIds.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var parameters = symbolChunk.Select((symbolId, index) =>
            {
                var name = "$symbol" + index;
                command.Parameters.AddWithValue(name, symbolId);
                return name;
            }).ToArray();
            command.CommandText = $"""
                SELECT COUNT(*)
                FROM symbols AS symbol
                INNER JOIN code_snapshots AS code_snapshot ON code_snapshot.snapshot_id = symbol.snapshot_id
                INNER JOIN environment_snapshots AS environment ON environment.snapshot_id = code_snapshot.environment_snapshot_id
                INNER JOIN index_runs AS code_index
                    ON code_index.index_id = $codeIndex
                   AND code_index.snapshot_id = symbol.snapshot_id
                WHERE symbol.symbol_id IN ({string.Join(',', parameters)})
                  AND symbol.snapshot_id = $codeSnapshot
                  AND symbol.kind = 'Type'
                  AND symbol.canonical_key LIKE 'ScheduleI:Installed:Type:%'
                  AND code_snapshot.codebase = 'ScheduleI'
                  AND code_snapshot.channel = 'Installed'
                  AND environment.build_id = $build
                  AND code_index.status = 'Completed';
                """;
            command.Parameters.AddWithValue("$codeIndex", snapshot.CodeIndexId);
            command.Parameters.AddWithValue("$codeSnapshot", snapshot.CodeSnapshotId);
            command.Parameters.AddWithValue("$build", snapshot.BuildId);
            var validated = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (validated != symbolChunk.Length)
            {
                throw new InvalidOperationException(
                    "Serialized reference target symbols must belong to the scene snapshot's exact completed Schedule I Installed code index and build.");
            }
        }
    }

    private static async Task<SceneSnapshotRecord?> GetRunningSceneSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SnapshotSelectSql + " WHERE scene_snapshot_id = $id AND status = 'Running';";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSceneSnapshot(reader) : null;
    }

    private static void AddSnapshotParameters(SqliteCommand command, SceneSnapshotRecord snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.SceneSnapshotId);
        command.Parameters.AddWithValue("$build", snapshot.BuildId);
        command.Parameters.AddWithValue("$extraction", snapshot.ExtractionId);
        command.Parameters.AddWithValue("$input", snapshot.InputSnapshotId);
        command.Parameters.AddWithValue("$codeSnapshot", snapshot.CodeSnapshotId);
        command.Parameters.AddWithValue("$codeIndex", snapshot.CodeIndexId);
        command.Parameters.AddWithValue("$parserId", snapshot.ParserId);
        command.Parameters.AddWithValue("$parserVersion", snapshot.ParserVersion);
        command.Parameters.AddWithValue("$digest", snapshot.ContainerManifestDigest);
        command.Parameters.AddWithValue("$recovery", snapshot.RecoveryStatus.ToString());
        command.Parameters.AddWithValue("$started", snapshot.StartedAtUtc);
    }

    private static async Task InsertContainerAsync(SqliteConnection connection, SqliteTransaction transaction, SceneContainerRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO scene_containers(container_id, scene_snapshot_id, relative_path, container_kind, unity_version, serialized_file_version, byte_count, sha256, sidecar_manifest) VALUES ($id,$snapshot,$path,$kind,$unity,$version,$bytes,$sha,$manifest);";
        command.Parameters.AddWithValue("$id", row.ContainerId); command.Parameters.AddWithValue("$snapshot", row.SceneSnapshotId); command.Parameters.AddWithValue("$path", row.RelativePath); command.Parameters.AddWithValue("$kind", row.ContainerKind); command.Parameters.AddWithValue("$unity", row.UnityVersion); command.Parameters.AddWithValue("$version", row.SerializedFileVersion); command.Parameters.AddWithValue("$bytes", row.ByteCount); command.Parameters.AddWithValue("$sha", row.Sha256); command.Parameters.AddWithValue("$manifest", row.SidecarManifest);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, SceneDocumentRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO scenes(scene_id, scene_snapshot_id, container_id, kind, name, source_local_file_id, object_count, root_count, recovery_status) VALUES ($id,$snapshot,$container,$kind,$name,$local,$objects,$roots,$recovery);";
        command.Parameters.AddWithValue("$id", row.SceneId); command.Parameters.AddWithValue("$snapshot", row.SceneSnapshotId); command.Parameters.AddWithValue("$container", row.ContainerId); command.Parameters.AddWithValue("$kind", row.Kind.ToString()); command.Parameters.AddWithValue("$name", row.Name); command.Parameters.AddWithValue("$local", (object?)row.SourceLocalFileId ?? DBNull.Value); command.Parameters.AddWithValue("$objects", row.ObjectCount); command.Parameters.AddWithValue("$roots", row.RootCount); command.Parameters.AddWithValue("$recovery", row.RecoveryStatus.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertGameObjectAsync(SqliteConnection connection, SqliteTransaction transaction, string sceneSnapshotId, SceneGameObjectRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO game_objects(game_object_id, scene_id, scene_snapshot_id, container_id, local_file_id, name, active, layer, tag, recovery_status) VALUES ($id,$scene,$snapshot,$container,$local,$name,$active,$layer,$tag,$recovery);";
        command.Parameters.AddWithValue("$id", row.GameObjectId); command.Parameters.AddWithValue("$scene", row.SceneId); command.Parameters.AddWithValue("$snapshot", sceneSnapshotId); command.Parameters.AddWithValue("$container", row.ContainerId); command.Parameters.AddWithValue("$local", row.LocalFileId); command.Parameters.AddWithValue("$name", row.Name); command.Parameters.AddWithValue("$active", row.Active is null ? DBNull.Value : row.Active.Value ? 1 : 0); command.Parameters.AddWithValue("$layer", (object?)row.Layer ?? DBNull.Value); command.Parameters.AddWithValue("$tag", (object?)row.Tag ?? DBNull.Value); command.Parameters.AddWithValue("$recovery", row.RecoveryStatus.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTransformAsync(SqliteConnection connection, SqliteTransaction transaction, SceneTransformRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO transforms(game_object_id, parent_game_object_id, sibling_index, position_x, position_y, position_z, rotation_x, rotation_y, rotation_z, rotation_w, scale_x, scale_y, scale_z, recovery_status) VALUES ($gameObject,$parent,$sibling,$px,$py,$pz,$rx,$ry,$rz,$rw,$sx,$sy,$sz,$recovery);";
        command.Parameters.AddWithValue("$gameObject", row.GameObjectId); command.Parameters.AddWithValue("$parent", (object?)row.ParentGameObjectId ?? DBNull.Value); command.Parameters.AddWithValue("$sibling", (object?)row.SiblingIndex ?? DBNull.Value); command.Parameters.AddWithValue("$px", (object?)row.PositionX ?? DBNull.Value); command.Parameters.AddWithValue("$py", (object?)row.PositionY ?? DBNull.Value); command.Parameters.AddWithValue("$pz", (object?)row.PositionZ ?? DBNull.Value); command.Parameters.AddWithValue("$rx", (object?)row.RotationX ?? DBNull.Value); command.Parameters.AddWithValue("$ry", (object?)row.RotationY ?? DBNull.Value); command.Parameters.AddWithValue("$rz", (object?)row.RotationZ ?? DBNull.Value); command.Parameters.AddWithValue("$rw", (object?)row.RotationW ?? DBNull.Value); command.Parameters.AddWithValue("$sx", (object?)row.ScaleX ?? DBNull.Value); command.Parameters.AddWithValue("$sy", (object?)row.ScaleY ?? DBNull.Value); command.Parameters.AddWithValue("$sz", (object?)row.ScaleZ ?? DBNull.Value); command.Parameters.AddWithValue("$recovery", row.RecoveryStatus.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertComponentAsync(SqliteConnection connection, SqliteTransaction transaction, SceneComponentRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO components(component_id, game_object_id, container_id, local_file_id, unity_class_id, kind, script_assembly, script_namespace, script_class, resolved_type_symbol_id, resolved_code_index_id, type_resolution_status, recovery_status) VALUES ($id,$gameObject,$container,$local,$class,$kind,$assembly,$namespace,$scriptClass,$symbol,$index,$resolution,$recovery);";
        command.Parameters.AddWithValue("$id", row.ComponentId); command.Parameters.AddWithValue("$gameObject", row.GameObjectId); command.Parameters.AddWithValue("$container", row.ContainerId); command.Parameters.AddWithValue("$local", row.LocalFileId); command.Parameters.AddWithValue("$class", row.UnityClassId); command.Parameters.AddWithValue("$kind", row.Kind); command.Parameters.AddWithValue("$assembly", (object?)row.ScriptAssembly ?? DBNull.Value); command.Parameters.AddWithValue("$namespace", (object?)row.ScriptNamespace ?? DBNull.Value); command.Parameters.AddWithValue("$scriptClass", (object?)row.ScriptClass ?? DBNull.Value); command.Parameters.AddWithValue("$symbol", (object?)row.ResolvedTypeSymbolId ?? DBNull.Value); command.Parameters.AddWithValue("$index", (object?)row.ResolvedCodeIndexId ?? DBNull.Value); command.Parameters.AddWithValue("$resolution", row.TypeResolutionStatus.ToString()); command.Parameters.AddWithValue("$recovery", row.RecoveryStatus.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReferenceAsync(SqliteConnection connection, SqliteTransaction transaction, SceneReferenceRecord row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO serialized_refs(reference_id, scene_snapshot_id, source_component_id, field_path, declared_type, source_container_id, source_local_file_id, target_container_id, target_local_file_id, target_game_object_id, target_component_id, target_symbol_id, target_text, resolution_status, evidence, recovery_status) VALUES ($id,$snapshot,$sourceComponent,$field,$declared,$sourceContainer,$sourceLocal,$targetContainer,$targetLocal,$targetGameObject,$targetComponent,$targetSymbol,$targetText,$resolution,$evidence,$recovery);";
        command.Parameters.AddWithValue("$id", row.ReferenceId); command.Parameters.AddWithValue("$snapshot", row.SceneSnapshotId); command.Parameters.AddWithValue("$sourceComponent", (object?)row.SourceComponentId ?? DBNull.Value); command.Parameters.AddWithValue("$field", (object?)row.FieldPath ?? DBNull.Value); command.Parameters.AddWithValue("$declared", (object?)row.DeclaredType ?? DBNull.Value); command.Parameters.AddWithValue("$sourceContainer", row.SourceContainerId); command.Parameters.AddWithValue("$sourceLocal", row.SourceLocalFileId); command.Parameters.AddWithValue("$targetContainer", (object?)row.TargetContainerId ?? DBNull.Value); command.Parameters.AddWithValue("$targetLocal", (object?)row.TargetLocalFileId ?? DBNull.Value); command.Parameters.AddWithValue("$targetGameObject", (object?)row.TargetGameObjectId ?? DBNull.Value); command.Parameters.AddWithValue("$targetComponent", (object?)row.TargetComponentId ?? DBNull.Value); command.Parameters.AddWithValue("$targetSymbol", (object?)row.TargetSymbolId ?? DBNull.Value); command.Parameters.AddWithValue("$targetText", (object?)row.TargetText ?? DBNull.Value); command.Parameters.AddWithValue("$resolution", row.ResolutionStatus.ToString()); command.Parameters.AddWithValue("$evidence", row.Evidence); command.Parameters.AddWithValue("$recovery", row.RecoveryStatus.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddPageParameters(SqliteCommand command, params object?[] arguments)
    {
        var names = new[] { "$snapshot", "$kind", "$query", "$limit" };
        for (var index = 0; index < arguments.Length; index++)
            command.Parameters.AddWithValue(names[index], arguments[index] ?? DBNull.Value);
    }

    private static void AddPageParameters(SqliteCommand command, string snapshot, string? scene, string? parent, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot); command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value); command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value); command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value); command.Parameters.AddWithValue("$limit", limit);
    }

    private static void AddPageParameters(SqliteCommand command, string snapshot, string? scene, string? gameObject, string? kind, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot); command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value); command.Parameters.AddWithValue("$gameObject", (object?)gameObject ?? DBNull.Value); command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value); command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value); command.Parameters.AddWithValue("$limit", limit);
    }

    private static void AddReferencePageParameters(SqliteCommand command, string snapshot, string? scene, string? gameObject, string? component, string? query, int limit)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot); command.Parameters.AddWithValue("$scene", (object?)scene ?? DBNull.Value); command.Parameters.AddWithValue("$gameObject", (object?)gameObject ?? DBNull.Value); command.Parameters.AddWithValue("$component", (object?)component ?? DBNull.Value); command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value); command.Parameters.AddWithValue("$limit", limit);
    }

    private static string EscapeSceneLikePattern(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static SceneSnapshotRecord ReadSceneSnapshot(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), Enum.Parse<SceneSnapshotStatus>(reader.GetString(9)), Enum.Parse<SceneRecoveryStatus>(reader.GetString(10)), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14));
    private static SceneContainerRecord ReadContainer(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8));
    private static SceneDocumentRecord ReadDocument(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Enum.Parse<SceneDocumentKind>(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7), Enum.Parse<SceneRecoveryStatus>(reader.GetString(8)));
    private static SceneGameObjectRecord ReadGameObject(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), Enum.Parse<SceneRecoveryStatus>(reader.GetString(8)));
    private static SceneComponentRecord ReadComponent(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), Enum.Parse<SceneResolutionStatus>(reader.GetString(11)), Enum.Parse<SceneRecoveryStatus>(reader.GetString(12)));
    private static SceneReferenceRecord ReadReference(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), Enum.Parse<SceneResolutionStatus>(reader.GetString(13)), reader.GetString(14), Enum.Parse<SceneRecoveryStatus>(reader.GetString(15)));
}
