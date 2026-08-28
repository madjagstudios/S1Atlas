using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    public async Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(
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
        var result = new List<IndexSymbolRecord>(ids.Length);
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

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadSymbol(reader));
        }

        return result
            .OrderBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken) =>
        GetCompletedRelationshipsByEndpointAsync(indexId, symbolId, source: true, cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken) =>
        GetCompletedRelationshipsByEndpointAsync(indexId, symbolId, source: false, cancellationToken);

    public async Task<int> CountCompletedRelationshipsByTargetTextAsync(
        string indexId,
        string targetText,
        RelationshipTargetTextMatchMode matchMode,
        string relationshipKind,
        CancellationToken cancellationToken)
    {
        ValidateTargetTextRelationshipQuery(indexId, targetText, matchMode, relationshipKind);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = matchMode == RelationshipTargetTextMatchMode.Exact
            ? """
              SELECT COUNT(*)
              FROM index_runs AS run
              INNER JOIN relationships AS relationship INDEXED BY ix_relationships_snapshot_kind_target_text
                  ON relationship.snapshot_id = run.snapshot_id
              WHERE run.index_id = $indexId
                AND run.status = 'Completed'
                AND relationship.relationship_kind = $relationshipKind
                AND relationship.target_text = $targetText COLLATE BINARY;
              """
            : """
              SELECT COUNT(*)
              FROM index_runs AS run
              INNER JOIN relationships AS relationship INDEXED BY ix_relationships_snapshot_kind_target_text
                  ON relationship.snapshot_id = run.snapshot_id
              WHERE run.index_id = $indexId
                AND run.status = 'Completed'
                AND relationship.relationship_kind = $relationshipKind
                AND (
                    relationship.target_text = $targetText COLLATE BINARY
                    OR (
                        relationship.target_text >= $prefixLower COLLATE BINARY
                        AND relationship.target_text < $prefixUpper COLLATE BINARY
                    )
                );
              """;
        AddTargetTextRelationshipParameters(command, indexId, targetText, relationshipKind, matchMode);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(
        string indexId,
        string targetText,
        RelationshipTargetTextMatchMode matchMode,
        string relationshipKind,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateTargetTextRelationshipQuery(indexId, targetText, matchMode, relationshipKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = matchMode == RelationshipTargetTextMatchMode.Exact
            ? """
              SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                     relationship.target_symbol_id, relationship.target_text,
                     relationship.relationship_kind, relationship.evidence
              FROM index_runs AS run
              INNER JOIN relationships AS relationship INDEXED BY ix_relationships_snapshot_kind_target_text
                  ON relationship.snapshot_id = run.snapshot_id
              WHERE run.index_id = $indexId
                AND run.status = 'Completed'
                AND relationship.relationship_kind = $relationshipKind
                AND relationship.target_text = $targetText COLLATE BINARY
              ORDER BY relationship.target_text COLLATE BINARY,
                       relationship.relationship_id COLLATE BINARY
              LIMIT $limit;
              """
            : """
              SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                     relationship.target_symbol_id, relationship.target_text,
                     relationship.relationship_kind, relationship.evidence
              FROM index_runs AS run
              INNER JOIN relationships AS relationship INDEXED BY ix_relationships_snapshot_kind_target_text
                  ON relationship.snapshot_id = run.snapshot_id
              WHERE run.index_id = $indexId
                AND run.status = 'Completed'
                AND relationship.relationship_kind = $relationshipKind
                AND (
                    relationship.target_text = $targetText COLLATE BINARY
                    OR (
                        relationship.target_text >= $prefixLower COLLATE BINARY
                        AND relationship.target_text < $prefixUpper COLLATE BINARY
                    )
                )
              ORDER BY relationship.target_text COLLATE BINARY,
                       relationship.relationship_id COLLATE BINARY
              LIMIT $limit;
              """;
        AddTargetTextRelationshipParameters(command, indexId, targetText, relationshipKind, matchMode);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<IndexRelationshipRecord>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRelationshipQueryRecord(reader));
        return result;
    }

    public async Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        string relationshipKind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipKind);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM index_runs AS run
            INNER JOIN relationships AS relationship INDEXED BY ix_relationships_target_kind
                ON relationship.snapshot_id = run.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND relationship.target_symbol_id = $symbolId
              AND relationship.relationship_kind = $relationshipKind;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$relationshipKind", relationshipKind);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        string relationshipKind,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                   relationship.target_symbol_id, relationship.target_text,
                   relationship.relationship_kind, relationship.evidence
            FROM index_runs AS run
            INNER JOIN relationships AS relationship INDEXED BY ix_relationships_target_kind
                ON relationship.snapshot_id = run.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND relationship.target_symbol_id = $symbolId
              AND relationship.relationship_kind = $relationshipKind
            ORDER BY relationship.relationship_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$relationshipKind", relationshipKind);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<IndexRelationshipRecord>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRelationshipQueryRecord(reader));
        return result;
    }

    private async Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByEndpointAsync(
        string indexId,
        string symbolId,
        bool source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT relationship.relationship_id, relationship.snapshot_id, relationship.source_symbol_id,
                   relationship.target_symbol_id, relationship.target_text,
                   relationship.relationship_kind, relationship.evidence
            FROM relationships AS relationship
            INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND relationship.{(source ? "source_symbol_id" : "target_symbol_id")} = $symbolId
            ORDER BY relationship.relationship_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);

        var result = new List<IndexRelationshipRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRelationshipQueryRecord(reader));
        return result;
    }

    private static void ValidateTargetTextRelationshipQuery(
        string indexId,
        string targetText,
        RelationshipTargetTextMatchMode matchMode,
        string relationshipKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetText);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipKind);
        if (!Enum.IsDefined(matchMode))
            throw new ArgumentOutOfRangeException(nameof(matchMode));
    }

    private static void AddTargetTextRelationshipParameters(
        SqliteCommand command,
        string indexId,
        string targetText,
        string relationshipKind,
        RelationshipTargetTextMatchMode matchMode)
    {
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$targetText", targetText);
        command.Parameters.AddWithValue("$relationshipKind", relationshipKind);
        if (matchMode == RelationshipTargetTextMatchMode.Prefix)
        {
            var prefixLower = targetText.EndsWith(')') ? targetText : targetText + "(";
            command.Parameters.AddWithValue("$prefixLower", prefixLower);
            command.Parameters.AddWithValue("$prefixUpper", prefixLower + "\uffff");
        }
    }

    private static IndexRelationshipRecord ReadRelationshipQueryRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
}
