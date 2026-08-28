using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Storage.Sqlite;

/// <summary>
/// Shared SQL and parameter binding for target-text and target-symbol relationship queries.
/// The read-only and read/write repositories run identical statements, so the text and the
/// prefix-range binding live here to keep the two paths from silently diverging.
/// </summary>
internal static class RelationshipQuerySql
{
    internal const string CountByTargetTextExact = """
        SELECT COUNT(*)
        FROM index_runs AS run
        INNER JOIN relationships AS relationship INDEXED BY ix_relationships_snapshot_kind_target_text
            ON relationship.snapshot_id = run.snapshot_id
        WHERE run.index_id = $indexId
          AND run.status = 'Completed'
          AND relationship.relationship_kind = $relationshipKind
          AND relationship.target_text = $targetText COLLATE BINARY;
        """;

    internal const string CountByTargetTextPrefix = """
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

    internal const string SelectByTargetTextExact = """
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
        """;

    internal const string SelectByTargetTextPrefix = """
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

    internal const string CountByTargetSymbol = """
        SELECT COUNT(*)
        FROM index_runs AS run
        INNER JOIN relationships AS relationship INDEXED BY ix_relationships_target_kind
            ON relationship.snapshot_id = run.snapshot_id
        WHERE run.index_id = $indexId
          AND run.status = 'Completed'
          AND relationship.target_symbol_id = $symbolId
          AND relationship.relationship_kind = $relationshipKind;
        """;

    internal const string SelectByTargetSymbol = """
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

    internal static string CountByTargetText(RelationshipTargetTextMatchMode matchMode) =>
        matchMode == RelationshipTargetTextMatchMode.Exact ? CountByTargetTextExact : CountByTargetTextPrefix;

    internal static string SelectByTargetText(RelationshipTargetTextMatchMode matchMode) =>
        matchMode == RelationshipTargetTextMatchMode.Exact ? SelectByTargetTextExact : SelectByTargetTextPrefix;

    internal static void ValidateTargetText(
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

    internal static void ValidateTargetSymbol(string indexId, string symbolId, string relationshipKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipKind);
    }

    internal static void AddTargetTextParameters(
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
            command.Parameters.AddWithValue("$prefixUpper", PrefixUpperBound(prefixLower));
        }
    }

    internal static void AddTargetSymbolParameters(
        SqliteCommand command,
        string indexId,
        string symbolId,
        string relationshipKind)
    {
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$relationshipKind", relationshipKind);
    }

    /// <summary>
    /// Exclusive upper bound for a BINARY-collated prefix scan: the smallest string strictly
    /// greater than every string beginning with <paramref name="prefixLower"/>. Incrementing the
    /// final code point covers all suffixes — including supplementary-plane characters — whereas a
    /// fixed sentinel such as "￿" would exclude targets whose next byte sorts above it.
    /// </summary>
    private static string PrefixUpperBound(string prefixLower) =>
        prefixLower[..^1] + (char)(prefixLower[^1] + 1);
}
