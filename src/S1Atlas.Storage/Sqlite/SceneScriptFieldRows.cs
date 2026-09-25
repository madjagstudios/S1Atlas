using Microsoft.Data.Sqlite;
using S1Atlas.Core.Scenes;

namespace S1Atlas.Storage.Sqlite;

// SQL and row mappers for script field sets and scriptable assets, shared by the writable and
// read-only repositories so the published-snapshot guard and column order cannot drift apart.
internal static class SceneScriptFieldRows
{
    public const string SelectFieldSetSql = """
        SELECT field_set.owner_id, field_set.scene_snapshot_id, field_set.owner_kind, field_set.status,
               field_set.unavailable_reason, field_set.truncated, field_set.fields_json
        FROM script_field_sets AS field_set
        INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = field_set.scene_snapshot_id
        WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL
          AND field_set.scene_snapshot_id = $snapshot AND field_set.owner_id = $owner;
        """;

    private const string AssetColumns = """
        SELECT asset.asset_id, asset.scene_snapshot_id, asset.container_id, asset.local_file_id, asset.name,
               asset.script_assembly, asset.script_namespace, asset.script_class, asset.resolved_type_symbol_id,
               asset.resolved_code_index_id, asset.type_resolution_status, asset.recovery_status
        FROM scriptable_assets AS asset
        INNER JOIN scene_snapshots AS snapshot ON snapshot.scene_snapshot_id = asset.scene_snapshot_id
        WHERE snapshot.status = 'Completed' AND snapshot.published_at_utc IS NOT NULL
          AND asset.scene_snapshot_id = $snapshot
        """;

    public const string SelectAssetByIdSql = AssetColumns + " AND asset.asset_id = $id;";

    // Exact asset name, or exact Namespace.Class (Class alone when the namespace is empty).
    public const string FindAssetsSql = AssetColumns + """

          AND (asset.name = $selector COLLATE BINARY
               OR (CASE WHEN asset.script_namespace IS NULL OR asset.script_namespace = '' THEN asset.script_class
                        ELSE asset.script_namespace || '.' || asset.script_class END) = $selector COLLATE BINARY)
        ORDER BY asset.name, asset.asset_id
        LIMIT $limit;
        """;

    public static SceneScriptFieldSetRecord ReadFieldSet(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<SceneScriptFieldOwnerKind>(reader.GetString(2)),
            Enum.Parse<SceneScriptFieldSetStatus>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5) != 0,
            SceneScriptFieldJson.Deserialize(reader.GetString(6)));

    public static SceneScriptableAssetRecord ReadAsset(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            Enum.Parse<SceneResolutionStatus>(reader.GetString(10)),
            Enum.Parse<SceneRecoveryStatus>(reader.GetString(11)));
}
