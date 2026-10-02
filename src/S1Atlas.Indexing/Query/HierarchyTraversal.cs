using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

internal enum HierarchyQueryMode
{
    Overrides,
    OverriddenBy,
    Derived
}

internal static class HierarchyTraversal
{
    public const int FullChainDepth = int.MaxValue;

    public static (IReadOnlyList<string> Kinds, bool Incoming, string Direction) Plan(HierarchyQueryMode mode) => mode switch
    {
        HierarchyQueryMode.Overrides => ([nameof(RelationshipKind.Overrides), nameof(RelationshipKind.ImplementsMethod)], false, "Outgoing"),
        HierarchyQueryMode.OverriddenBy => ([nameof(RelationshipKind.Overrides), nameof(RelationshipKind.ImplementsMethod)], true, "Incoming"),
        HierarchyQueryMode.Derived => ([nameof(RelationshipKind.Inherits), nameof(RelationshipKind.ImplementsInterface)], true, "Incoming"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static void ValidateDepth(int depth)
    {
        if (depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth), "The hierarchy depth must be positive.");
    }

    public static void ValidateOffset(int offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "The hierarchy offset must not be negative.");
    }

    public static async Task<IReadOnlyList<(IndexRelationshipRecord Edge, int Depth)>> CollectAsync(
        IIndexRepository repository,
        string indexId,
        string startSymbolId,
        IReadOnlyList<string> kinds,
        bool incoming,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(startSymbolId);
        ArgumentNullException.ThrowIfNull(kinds);

        var visited = new HashSet<string>([startSymbolId], StringComparer.Ordinal);
        var frontier = new Queue<(string SymbolId, int Depth)>();
        frontier.Enqueue((startSymbolId, 0));
        var collected = new List<(IndexRelationshipRecord Edge, int Depth)>();
        while (frontier.Count > 0)
        {
            var (symbolId, level) = frontier.Dequeue();
            if (level >= depth)
                continue;
            foreach (var edge in await EdgesAsync(repository, indexId, symbolId, kinds, incoming, cancellationToken))
            {
                collected.Add((edge, level + 1));
                var next = incoming ? edge.SourceSymbolId : edge.TargetSymbolId;
                if (next is not null && visited.Add(next))
                    frontier.Enqueue((next, level + 1));
            }
        }

        return collected;
    }

    public static IReadOnlyList<HierarchyNodeQueryResult> Order(
        IReadOnlyList<HierarchyNodeQueryResult> nodes,
        bool incoming) =>
        nodes
            .OrderBy(node => node.Depth)
            .ThenBy(node => NodeName(node.Edge, incoming), StringComparer.Ordinal)
            .ThenBy(node => node.Edge.RelationshipId, StringComparer.Ordinal)
            .ToArray();

    private static async Task<IReadOnlyList<IndexRelationshipRecord>> EdgesAsync(
        IIndexRepository repository,
        string indexId,
        string symbolId,
        IReadOnlyList<string> kinds,
        bool incoming,
        CancellationToken cancellationToken)
    {
        if (incoming)
        {
            var edges = new List<IndexRelationshipRecord>();
            foreach (var kind in kinds)
            {
                edges.AddRange(await repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                    indexId, symbolId, kind, int.MaxValue, cancellationToken));
            }

            return edges;
        }

        return (await repository.GetCompletedRelationshipsBySourceSymbolIdAsync(indexId, symbolId, cancellationToken))
            .Where(edge => kinds.Contains(edge.Kind))
            .ToArray();
    }

    private static string NodeName(RelationshipQueryResult edge, bool incoming)
    {
        var endpoint = incoming ? edge.Source : edge.Target;
        return endpoint.QualifiedName ?? endpoint.RawText ?? endpoint.SymbolId ?? string.Empty;
    }
}
