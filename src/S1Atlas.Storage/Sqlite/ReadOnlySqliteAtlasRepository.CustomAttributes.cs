using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class ReadOnlySqliteAtlasRepository
{
    public Task<IReadOnlyList<IndexCustomAttributeRecord>> GetCompletedAttributesBySymbolIdsAsync(
        string indexId,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentNullException.ThrowIfNull(symbolIds);
            var ids = symbolIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) return (IReadOnlyList<IndexCustomAttributeRecord>)Array.Empty<IndexCustomAttributeRecord>();
            const int chunkSize = 500;
            var result = new List<IndexCustomAttributeRecord>(ids.Length);
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
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) result.Add(ReadCustomAttribute(reader));
            }
            return (IReadOnlyList<IndexCustomAttributeRecord>)result.OrderBy(attribute => attribute.AttributeId, StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    public Task<int> CountCompletedAttributesByTypeAsync(
        string indexId,
        string attributeType,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(attributeType);
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
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<IndexCustomAttributeRecord>> FindCompletedAttributesByTypeAsync(
        string indexId,
        string attributeType,
        int limit,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(attributeType);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
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
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(ReadCustomAttribute(reader));
            return (IReadOnlyList<IndexCustomAttributeRecord>)result;
        }, cancellationToken);

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
