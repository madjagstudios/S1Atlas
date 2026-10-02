using System.Text.Json;

namespace S1Atlas.Indexing.Tests.Relationships.Parity;

public sealed record ParityEdge(string Symbol, string Reason);

public sealed record ParityKnownGap(string Symbol, string Relation, string Reason, string Ticket);

public sealed record ParityTarget(
    string Target,
    IReadOnlyList<ParityEdge> Callers,
    IReadOnlyList<ParityEdge> Callees,
    IReadOnlyList<ParityEdge> Readers,
    IReadOnlyList<ParityEdge> Writers,
    IReadOnlyList<ParityKnownGap> KnownGaps,
    IReadOnlyList<string> Overriders,
    IReadOnlyList<string> Implementers);

public sealed record ParityOracle(IReadOnlyList<ParityTarget> Targets)
{
    public static ParityOracle Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonDocument.Parse(stream);
        var targets = new List<ParityTarget>();
        foreach (var element in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            targets.Add(new ParityTarget(
                element.GetProperty("target").GetString()!,
                ReadEdges(element, "callers"),
                ReadEdges(element, "callees"),
                ReadEdges(element, "readers"),
                ReadEdges(element, "writers"),
                ReadGaps(element),
                ReadStrings(element, "overriders"),
                ReadStrings(element, "implementers")));
        }

        return new ParityOracle(targets);
    }

    private static IReadOnlyList<ParityEdge> ReadEdges(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array))
            return [];
        return array.EnumerateArray()
            .Select(edge => new ParityEdge(
                edge.GetProperty("symbol").GetString()!,
                edge.GetProperty("reason").GetString()!))
            .ToList();
    }

    private static IReadOnlyList<ParityKnownGap> ReadGaps(JsonElement element)
    {
        if (!element.TryGetProperty("knownGaps", out var array))
            return [];
        return array.EnumerateArray()
            .Select(gap => new ParityKnownGap(
                gap.GetProperty("symbol").GetString()!,
                gap.GetProperty("relation").GetString()!,
                gap.GetProperty("reason").GetString()!,
                gap.GetProperty("ticket").GetString()!))
            .ToList();
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array))
            return [];
        return array.EnumerateArray().Select(item => item.GetString()!).ToList();
    }
}
