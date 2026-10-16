using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Scenes;

namespace S1Atlas.Storage.Sqlite;

// Scene graph list queries shared by the writable and read-only repositories, so the
// published-snapshot guard, filters, ordering and paging cannot drift apart (AT-49).
internal static class SceneGraphRows
{
    private const string PublishedSnapshot = "snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL";

    private const string GameObjectColumns =
        "game_object.game_object_id, game_object.scene_id, game_object.container_id, game_object.local_file_id, game_object.name, game_object.active, game_object.layer, game_object.tag, game_object.recovery_status";

    private const string ComponentColumns = """
        component.component_id, component.game_object_id, component.container_id, component.local_file_id,
               component.unity_class_id, component.kind, component.script_assembly, component.script_namespace,
               component.script_class, component.resolved_type_symbol_id, component.resolved_code_index_id,
               component.type_resolution_status, component.recovery_status
        """;

    // A selective filter that has an index (a parent object, an owning object, resolved type
    // symbols) is spliced in only when set, and CROSS JOIN then pins its table as the outer loop.
    // A "$x IS NULL OR" guard stops SQLite using the index, and the planner otherwise walks every
    // game object in the snapshot first: seconds on a real snapshot instead of milliseconds.
    private static string GameObjectFilter(GameObjectListQueryOptions options) => options.ParentGameObjectId is null
        ? $"""
            FROM game_objects AS game_object
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            WHERE {PublishedSnapshot} AND game_object.scene_snapshot_id = $snapshot
              AND ($scene IS NULL OR game_object.scene_id = $scene)
              AND ($query IS NULL OR game_object.name LIKE $query ESCAPE '\' COLLATE NOCASE)
            """
        : $"""
            FROM transforms AS transform
            CROSS JOIN game_objects AS game_object ON game_object.game_object_id = transform.game_object_id
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            WHERE {PublishedSnapshot} AND game_object.scene_snapshot_id = $snapshot
              AND transform.parent_game_object_id = $parent
              AND ($scene IS NULL OR game_object.scene_id = $scene)
              AND ($query IS NULL OR game_object.name LIKE $query ESCAPE '\' COLLATE NOCASE)
            """;

    // $type matches the Unity kind or the exact Namespace.Class (Class alone when the namespace
    // is empty), which scans the snapshot's components; $typeIds matches resolved type symbols in
    // the JSON ID array through ix_components_resolved_type_symbol.
    private static string ComponentFilter(ComponentListQueryOptions options) => $"""
        FROM components AS component
        {(options.TypeSymbolIds is null && options.GameObjectId is null ? "INNER JOIN" : "CROSS JOIN")} game_objects AS game_object ON game_object.game_object_id = component.game_object_id
        INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
        WHERE {PublishedSnapshot} AND game_object.scene_snapshot_id = $snapshot
          AND ($scene IS NULL OR game_object.scene_id = $scene)
          {(options.GameObjectId is null ? string.Empty : "AND component.game_object_id = $gameObject")}
          AND ($kind IS NULL OR component.kind = $kind)
          AND ($query IS NULL OR component.kind LIKE $query ESCAPE '\' COLLATE NOCASE)
          {(options.ExactType is null ? string.Empty : TypeTextFilter)}
          {(options.TypeSymbolIds is null ? string.Empty : "AND component.resolved_type_symbol_id IN (SELECT value FROM json_each($typeIds))")}
        """;

    private const string TypeTextFilter = """
        AND (component.kind = $type COLLATE BINARY
               OR CASE
                    WHEN component.script_class IS NULL THEN NULL
                    WHEN component.script_namespace IS NULL OR component.script_namespace = '' THEN component.script_class
                    ELSE component.script_namespace || '.' || component.script_class
                  END = $type COLLATE BINARY)
        """;

    private const string ReferenceFilter = $"""
        FROM serialized_refs AS reference
        LEFT JOIN components AS component ON component.component_id = reference.source_component_id
        LEFT JOIN game_objects AS component_game_object ON component_game_object.game_object_id = component.game_object_id
        LEFT JOIN game_objects AS source_game_object
          ON reference.source_component_id IS NULL
         AND source_game_object.scene_snapshot_id = reference.scene_snapshot_id
         AND source_game_object.container_id = reference.source_container_id
         AND source_game_object.local_file_id = reference.source_local_file_id
        INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = reference.scene_snapshot_id
        WHERE {PublishedSnapshot} AND reference.scene_snapshot_id = $snapshot
          AND ($scene IS NULL OR COALESCE(component_game_object.scene_id, source_game_object.scene_id) = $scene)
          AND ($gameObject IS NULL OR COALESCE(component.game_object_id, source_game_object.game_object_id) = $gameObject)
          AND ($component IS NULL OR reference.source_component_id = $component)
          AND ($query IS NULL OR reference.field_path LIKE $query ESCAPE '\' COLLATE NOCASE OR reference.target_text LIKE $query ESCAPE '\' COLLATE NOCASE)
        """;

    // Every decoded PPtr leaf whose resolved target is one of the JSON $targets IDs, with the
    // owner it belongs to. Only decoded field sets are read; coverage reports the rest.
    private const string IncomingSql = $"""
        WITH matched AS (
            SELECT field_set.owner_id AS owner_id, field_set.owner_kind AS owner_kind,
                   json_extract(field.value, '$.path') AS path,
                   json_extract(field.value, '$.typeName') AS type_name,
                   json_extract(field.value, '$.value') AS value,
                   json_extract(field.value, '$.target.id') AS target_id,
                   json_extract(field.value, '$.target.kind') AS target_kind
            FROM script_field_sets AS field_set
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = field_set.scene_snapshot_id,
                 json_each(field_set.fields_json) AS field
            WHERE {PublishedSnapshot} AND field_set.scene_snapshot_id = $snapshot AND field_set.status = 'Decoded'
              AND json_extract(field.value, '$.kind') = 'PPtr'
              AND json_extract(field.value, '$.target.id') IN (SELECT value FROM json_each($targets)))
        SELECT matched.owner_id, matched.owner_kind, component.game_object_id,
               COALESCE(game_object.name, asset.name),
               CASE
                 WHEN COALESCE(component.script_class, asset.script_class) IS NULL THEN component.kind
                 WHEN COALESCE(component.script_namespace, asset.script_namespace, '') = '' THEN COALESCE(component.script_class, asset.script_class)
                 ELSE COALESCE(component.script_namespace, asset.script_namespace) || '.' || COALESCE(component.script_class, asset.script_class)
               END,
               COALESCE(component.container_id, asset.container_id),
               matched.path, matched.type_name, matched.value, matched.target_id, matched.target_kind
        FROM matched
        LEFT JOIN components AS component ON matched.owner_kind = 'Component' AND component.component_id = matched.owner_id
        LEFT JOIN game_objects AS game_object ON game_object.game_object_id = component.game_object_id
        LEFT JOIN scriptable_assets AS asset ON matched.owner_kind = 'ScriptableAsset' AND asset.asset_id = matched.owner_id;
        """;

    // The source GameObjects (JSON $ids) that are $root or one of its descendants.
    private const string WithinHierarchySql = """
        WITH RECURSIVE chain(start_id, current_id) AS (
            SELECT value, value FROM json_each($ids)
            UNION
            SELECT chain.start_id, transform.parent_game_object_id
            FROM chain INNER JOIN transforms AS transform ON transform.game_object_id = chain.current_id
            WHERE transform.parent_game_object_id IS NOT NULL)
        SELECT DISTINCT start_id FROM chain WHERE current_id = $root;
        """;

    private const string CoverageSql = $"""
        SELECT field_set.status, field_set.truncated, COUNT(*)
        FROM script_field_sets AS field_set
        INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = field_set.scene_snapshot_id
        WHERE {PublishedSnapshot} AND field_set.scene_snapshot_id = $snapshot
        GROUP BY field_set.status, field_set.truncated;
        """;

    // Script owners (MonoBehaviour components and scriptable assets) whose fields were never
    // parsed, so they have no field set row at all.
    private const string NotParsedOwnerCountSql = $"""
        SELECT (SELECT COUNT(*)
                FROM game_objects AS game_object
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
                INNER JOIN components AS component ON component.game_object_id = game_object.game_object_id AND component.kind = 'MonoBehaviour'
                WHERE {PublishedSnapshot} AND game_object.scene_snapshot_id = $snapshot
                  AND NOT EXISTS (SELECT 1 FROM script_field_sets AS field_set WHERE field_set.owner_id = component.component_id))
             + (SELECT COUNT(*)
                FROM scriptable_assets AS asset
                INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = asset.scene_snapshot_id
                WHERE {PublishedSnapshot} AND asset.scene_snapshot_id = $snapshot
                  AND NOT EXISTS (SELECT 1 FROM script_field_sets AS field_set WHERE field_set.owner_id = asset.asset_id));
        """;

    public static async Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(SqliteConnection connection, GameObjectListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        void Bind(SqliteCommand command)
        {
            Add(command, "$snapshot", options.SceneSnapshotId);
            Add(command, "$scene", options.SceneId);
            Add(command, "$parent", options.ParentGameObjectId);
            Add(command, "$query", LikePattern(options.Query));
        }

        var filter = GameObjectFilter(options);
        var total = await CountAsync(connection, $"SELECT COUNT(*) {filter};", Bind, cancellationToken);
        var rows = await PageAsync(connection, $"""
            SELECT {GameObjectColumns}
            {filter}
            ORDER BY game_object.name COLLATE BINARY, game_object.game_object_id COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """, Bind, options.Limit, options.Offset, ReadGameObject, cancellationToken);
        return new ScenePageResult<SceneGameObjectRecord>(total, rows.Count, rows);
    }

    public static async Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(SqliteConnection connection, ComponentListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        void Bind(SqliteCommand command)
        {
            Add(command, "$snapshot", options.SceneSnapshotId);
            Add(command, "$scene", options.SceneId);
            Add(command, "$gameObject", options.GameObjectId);
            Add(command, "$kind", options.ExactKind);
            Add(command, "$query", LikePattern(options.Query));
            Add(command, "$type", options.ExactType);
            Add(command, "$typeIds", options.TypeSymbolIds is null ? null : JsonArray(options.TypeSymbolIds));
        }

        var filter = ComponentFilter(options);
        var total = await CountAsync(connection, $"SELECT COUNT(*) {filter};", Bind, cancellationToken);
        var rows = await PageAsync(connection, $"""
            SELECT {ComponentColumns}
            {filter}
            ORDER BY component.kind COLLATE BINARY, component.component_id COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """, Bind, options.Limit, options.Offset, ReadComponent, cancellationToken);
        return new ScenePageResult<SceneComponentRecord>(total, rows.Count, rows);
    }

    public static async Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(SqliteConnection connection, ReferenceListQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        void Bind(SqliteCommand command)
        {
            Add(command, "$snapshot", options.SceneSnapshotId);
            Add(command, "$scene", options.SceneId);
            Add(command, "$gameObject", options.GameObjectId);
            Add(command, "$component", options.SourceComponentId);
            Add(command, "$query", LikePattern(options.Query));
        }

        var total = await CountAsync(connection, $"SELECT COUNT(*) {ReferenceFilter};", Bind, cancellationToken);
        var unresolved = await CountAsync(connection, $"SELECT COUNT(*) {ReferenceFilter} AND reference.resolution_status <> 'Resolved';", Bind, cancellationToken);
        var rows = await PageAsync(connection, $"""
            SELECT reference.reference_id, reference.scene_snapshot_id, reference.source_component_id, reference.field_path, reference.declared_type,
                   reference.source_container_id, reference.source_local_file_id, reference.target_container_id, reference.target_local_file_id,
                   reference.target_game_object_id, reference.target_component_id, reference.target_symbol_id, reference.target_text,
                   reference.resolution_status, reference.evidence, reference.recovery_status
            {ReferenceFilter}
            ORDER BY reference.field_path COLLATE BINARY, reference.reference_id COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """, Bind, options.Limit, options.Offset, ReadReference, cancellationToken);
        return new ScenePageResult<SceneReferenceRecord>(total, rows.Count, rows, unresolved);
    }

    public static async Task<IReadOnlyList<SceneGameObjectRecord>> GetGameObjectsAsync(SqliteConnection connection, string sceneSnapshotId, IReadOnlyList<string> gameObjectIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentNullException.ThrowIfNull(gameObjectIds);
        if (gameObjectIds.Count == 0) return [];
        return await ReadAllAsync(connection, $"""
            SELECT {GameObjectColumns}
            FROM game_objects AS game_object
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = game_object.scene_snapshot_id
            WHERE {PublishedSnapshot} AND game_object.scene_snapshot_id = $snapshot
              AND game_object.game_object_id IN (SELECT value FROM json_each($ids))
            ORDER BY game_object.game_object_id COLLATE BINARY;
            """, command =>
        {
            Add(command, "$snapshot", sceneSnapshotId);
            Add(command, "$ids", JsonArray(gameObjectIds));
        }, ReadGameObject, cancellationToken);
    }

    public static async Task<IReadOnlyList<SceneScriptFieldSetRecord>> GetScriptFieldSetsAsync(SqliteConnection connection, string sceneSnapshotId, IReadOnlyList<string> ownerIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentNullException.ThrowIfNull(ownerIds);
        if (ownerIds.Count == 0) return [];
        return await ReadAllAsync(connection, $"""
            SELECT field_set.owner_id, field_set.scene_snapshot_id, field_set.owner_kind, field_set.status,
                   field_set.unavailable_reason, field_set.truncated, field_set.fields_json
            FROM script_field_sets AS field_set
            INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = field_set.scene_snapshot_id
            WHERE {PublishedSnapshot} AND field_set.scene_snapshot_id = $snapshot
              AND field_set.owner_id IN (SELECT value FROM json_each($ids))
            ORDER BY field_set.owner_id COLLATE BINARY;
            """, command =>
        {
            Add(command, "$snapshot", sceneSnapshotId);
            Add(command, "$ids", JsonArray(ownerIds));
        }, SceneScriptFieldRows.ReadFieldSet, cancellationToken);
    }

    public static async Task<SceneIncomingReferencePage> ListIncomingReferencesAsync(SqliteConnection connection, IncomingReferenceQueryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var matched = await ReadAllAsync(connection, IncomingSql, command =>
        {
            Add(command, "$snapshot", options.SceneSnapshotId);
            Add(command, "$targets", JsonArray(options.TargetIds));
        }, reader => new IncomingRow(
            reader.GetString(0),
            Enum.Parse<SceneScriptFieldOwnerKind>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            Enum.Parse<SceneScriptFieldTargetKind>(reader.GetString(10))), cancellationToken);

        var sourceObjects = matched.Select(row => row.SourceGameObjectId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var internalObjects = sourceObjects.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await ReadAllAsync(connection, WithinHierarchySql, command =>
            {
                Add(command, "$ids", JsonArray(sourceObjects));
                Add(command, "$root", options.HierarchyRootGameObjectId);
            }, reader => reader.GetString(0), cancellationToken)).ToHashSet(StringComparer.Ordinal);

        // Skip owner rows with no component or asset row behind them, so no container to report.
        var classified = matched
            .Where(row => row.SourceContainerId is not null)
            .Select(row => new SceneIncomingReferenceRecord(
                row.OwnerId, row.OwnerKind, row.SourceGameObjectId, row.SourceName, row.SourceType, row.SourceContainerId!,
                row.Path, row.TypeName, row.Value, row.TargetId, row.TargetKind,
                row.SourceGameObjectId is not null && internalObjects.Contains(row.SourceGameObjectId)))
            .OrderBy(row => row.WithinTarget)
            .ThenBy(row => row.SourceName, StringComparer.Ordinal)
            .ThenBy(row => row.SourceOwnerId, StringComparer.Ordinal)
            .ThenBy(row => row.FieldPath, StringComparer.Ordinal)
            .ToArray();
        var externalCount = classified.Count(row => !row.WithinTarget);
        var selected = options.ExternalOnly ? classified.Where(row => !row.WithinTarget).ToArray() : classified;
        var page = selected.Skip(options.Offset).Take(options.Limit).ToArray();
        return new SceneIncomingReferencePage(selected.Length, page.Length, page, externalCount, await CoverageAsync(connection, options.SceneSnapshotId, cancellationToken));
    }

    public static SceneGameObjectRecord ReadGameObject(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), Enum.Parse<SceneRecoveryStatus>(reader.GetString(8)));
    public static SceneComponentRecord ReadComponent(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), Enum.Parse<SceneResolutionStatus>(reader.GetString(11)), Enum.Parse<SceneRecoveryStatus>(reader.GetString(12)));
    public static SceneReferenceRecord ReadReference(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), Enum.Parse<SceneResolutionStatus>(reader.GetString(13)), reader.GetString(14), Enum.Parse<SceneRecoveryStatus>(reader.GetString(15)));

    private static async Task<SceneFieldSetCoverage> CoverageAsync(SqliteConnection connection, string sceneSnapshotId, CancellationToken cancellationToken)
    {
        var groups = await ReadAllAsync(connection, CoverageSql, command => Add(command, "$snapshot", sceneSnapshotId),
            reader => (Status: reader.GetString(0), Truncated: reader.GetInt64(1) != 0, Count: reader.GetInt32(2)), cancellationToken);
        var decoded = groups.Where(group => group.Status == nameof(SceneScriptFieldSetStatus.Decoded)).Sum(group => group.Count);
        var unavailable = groups.Where(group => group.Status == nameof(SceneScriptFieldSetStatus.Unavailable)).Sum(group => group.Count);
        var notParsed = await CountAsync(connection, NotParsedOwnerCountSql, command => Add(command, "$snapshot", sceneSnapshotId), cancellationToken);
        return new SceneFieldSetCoverage(decoded, unavailable, groups.Where(group => group.Truncated).Sum(group => group.Count), notParsed);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static Task<IReadOnlyList<T>> PageAsync<T>(SqliteConnection connection, string sql, Action<SqliteCommand> bind, int limit, int offset, Func<SqliteDataReader, T> read, CancellationToken cancellationToken) =>
        ReadAllAsync(connection, sql, command =>
        {
            bind(command);
            Add(command, "$limit", limit);
            Add(command, "$offset", offset);
        }, read, cancellationToken);

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteConnection connection, string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(read(reader));
        return rows;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string JsonArray(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static string? LikePattern(string? query) => query is null
        ? null
        : "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private sealed record IncomingRow(
        string OwnerId,
        SceneScriptFieldOwnerKind OwnerKind,
        string? SourceGameObjectId,
        string? SourceName,
        string? SourceType,
        string? SourceContainerId,
        string Path,
        string TypeName,
        string Value,
        string TargetId,
        SceneScriptFieldTargetKind TargetKind);
}
