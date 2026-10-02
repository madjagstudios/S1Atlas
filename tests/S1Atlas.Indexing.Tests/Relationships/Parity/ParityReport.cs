using System.Text;
using System.Text.Json;

namespace S1Atlas.Indexing.Tests.Relationships.Parity;

public enum ParityClassification
{
    Found,
    Missing,
    Extra,
    Mislabeled
}

public sealed record ParityDifference(
    string Target,
    string Relation,
    string Symbol,
    ParityClassification Classification,
    string Reason,
    string Ticket);

public sealed record ParityCategoryTotals(
    string Category,
    int Expected,
    int Found,
    int Missing,
    int Extra,
    int Mislabeled);

public sealed record ParityKnownGapUnmatched(
    string Target,
    string Relation,
    string Symbol,
    string Reason,
    string Ticket);

public sealed record ParityReport(
    IReadOnlyList<ParityDifference> Differences,
    IReadOnlyList<ParityCategoryTotals> Totals,
    IReadOnlyList<ParityKnownGapUnmatched> UnmatchedGaps,
    IReadOnlyList<string> RefsConsistencyNotes)
{
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Relationship Parity Report");
        builder.AppendLine();
        builder.AppendLine("## Totals by reason");
        builder.AppendLine();
        builder.AppendLine("| Reason | Expected | Found | Missing | Extra | Mislabeled |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var total in Totals.OrderBy(item => item.Category, StringComparer.Ordinal))
        {
            builder.AppendLine($"| {total.Category} | {total.Expected} | {total.Found} | {total.Missing} | {total.Extra} | {total.Mislabeled} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Differences");
        builder.AppendLine();
        var differences = Differences
            .Where(item => item.Classification != ParityClassification.Found)
            .OrderBy(item => item.Target, StringComparer.Ordinal)
            .ThenBy(item => item.Relation, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (differences.Length == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            builder.AppendLine("| Target | Relation | Symbol | Classification | Reason | Ticket |");
            builder.AppendLine("|---|---|---|---|---|---|");
            foreach (var difference in differences)
            {
                builder.AppendLine($"| {difference.Target} | {difference.Relation} | {difference.Symbol} | {difference.Classification.ToString().ToUpperInvariant()} | {difference.Reason} | {difference.Ticket} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Unmatched known gaps");
        builder.AppendLine();
        var gaps = UnmatchedGaps
            .OrderBy(item => item.Target, StringComparer.Ordinal)
            .ThenBy(item => item.Relation, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (gaps.Length == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            builder.AppendLine("| Target | Relation | Symbol | Reason | Ticket |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var gap in gaps)
            {
                builder.AppendLine($"| {gap.Target} | {gap.Relation} | {gap.Symbol} | {gap.Reason} | {gap.Ticket} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Refs consistency");
        builder.AppendLine();
        if (RefsConsistencyNotes.Count == 0)
        {
            builder.AppendLine("Consistent.");
        }
        else
        {
            foreach (var note in RefsConsistencyNotes.Order(StringComparer.Ordinal))
            {
                builder.AppendLine($"- {note}");
            }
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    public string ToJson()
    {
        var payload = new
        {
            totals = Totals.OrderBy(item => item.Category, StringComparer.Ordinal).ToArray(),
            differences = Differences
                .OrderBy(item => item.Target, StringComparer.Ordinal)
                .ThenBy(item => item.Relation, StringComparer.Ordinal)
                .ThenBy(item => item.Symbol, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.Target,
                    item.Relation,
                    item.Symbol,
                    classification = item.Classification.ToString().ToUpperInvariant(),
                    item.Reason,
                    item.Ticket
                })
                .ToArray(),
            unmatchedGaps = UnmatchedGaps
                .OrderBy(item => item.Target, StringComparer.Ordinal)
                .ThenBy(item => item.Relation, StringComparer.Ordinal)
                .ThenBy(item => item.Symbol, StringComparer.Ordinal)
                .ToArray(),
            refsConsistencyNotes = RefsConsistencyNotes.Order(StringComparer.Ordinal).ToArray()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }
}
