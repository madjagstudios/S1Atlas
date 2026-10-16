using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    public async Task<IReadOnlyList<IndexCustomAttributeRecord>> GetCompletedAttributesBySymbolIdsAsync(
        string indexId,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentNullException.ThrowIfNull(symbolIds);

        var ids = symbolIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return [];

        const int chunkSize = 500;
        var result = new List<IndexCustomAttributeRecord>(ids.Length);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        for (var offset = 0; offset < ids.Length; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToArray();
            await using var command = connection.CreateCommand();
            var parameterNames = new string[chunk.Length];
            for (var index = 0; index < chunk.Length; index++)
            {
                parameterNames[index] = "$symbol" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameterNames[index], chunk[index]);
            }

            command.CommandText = $"""
                SELECT attribute.attribute_id, attribute.snapshot_id, attribute.symbol_id,
                       attribute.target, attribute.parameter_index, attribute.parameter_name,
                       attribute.attribute_type, attribute.args_json
                FROM custom_attributes AS attribute
                INNER JOIN index_runs AS run ON run.snapshot_id = attribute.snapshot_id
                WHERE run.index_id = $indexId
                  AND run.status = 'Completed'
                  AND attribute.symbol_id IN ({string.Join(", ", parameterNames)});
                """;
            command.Parameters.AddWithValue("$indexId", indexId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadCustomAttribute(reader));
        }

        return result
            .OrderBy(attribute => attribute.AttributeId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<int> CountCompletedAttributesByTypeAsync(
        string indexId,
        string attributeType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeType);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM custom_attributes AS attribute
            INNER JOIN index_runs AS run ON run.snapshot_id = attribute.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND attribute.attribute_type = $attributeType COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$attributeType", attributeType);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexCustomAttributeRecord>> FindCompletedAttributesByTypeAsync(
        string indexId,
        string attributeType,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attribute.attribute_id, attribute.snapshot_id, attribute.symbol_id,
                   attribute.target, attribute.parameter_index, attribute.parameter_name,
                   attribute.attribute_type, attribute.args_json
            FROM custom_attributes AS attribute
            INNER JOIN index_runs AS run ON run.snapshot_id = attribute.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND attribute.attribute_type = $attributeType COLLATE NOCASE
            ORDER BY attribute.attribute_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$attributeType", attributeType);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<IndexCustomAttributeRecord>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadCustomAttribute(reader));
        return result;
    }

    private static async Task InsertCustomAttributesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<IndexCustomAttributeRecord> attributes,
        CancellationToken cancellationToken)
    {
        if (attributes.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO custom_attributes(attribute_id, snapshot_id, symbol_id, target, parameter_index, parameter_name, attribute_type, args_json) VALUES ($id,$snapshot,$symbol,$target,$parameterIndex,$parameterName,$attributeType,$argsJson);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        var symbol = command.Parameters.Add("$symbol", SqliteType.Text);
        var target = command.Parameters.Add("$target", SqliteType.Text);
        var parameterIndex = command.Parameters.Add("$parameterIndex", SqliteType.Integer);
        var parameterName = command.Parameters.Add("$parameterName", SqliteType.Text);
        var attributeType = command.Parameters.Add("$attributeType", SqliteType.Text);
        var argsJson = command.Parameters.Add("$argsJson", SqliteType.Text);
        command.Prepare();
        foreach (var attribute in attributes)
        {
            id.Value = attribute.AttributeId;
            snapshot.Value = attribute.SnapshotId;
            symbol.Value = attribute.SymbolId;
            target.Value = attribute.Target;
            parameterIndex.Value = (object?)attribute.ParameterIndex ?? DBNull.Value;
            parameterName.Value = (object?)attribute.ParameterName ?? DBNull.Value;
            attributeType.Value = attribute.AttributeType;
            argsJson.Value = attribute.ArgsJson;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static IndexCustomAttributeRecord ReadCustomAttribute(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));
}
