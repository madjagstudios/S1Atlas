using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed record DerivedCallerEdge(
    IndexRelationshipRecord Edge,
    IReadOnlyList<string> Routes);

public sealed record MergedCallerPage(
    IReadOnlyList<RelationshipQueryResult> Relationships,
    int ExactCount,
    int DerivedCount);

/// <summary>
/// May-dispatch expansion for caller queries. Callers of every slot the
/// selected method fills (its override chain plus implemented interface
/// methods) may reach the selected method at runtime, so they are reported
/// as derived rows with the route they were reached by. Only virtual call
/// sites (<c>CallsVirtual</c>) are followed: non-virtual base calls are
/// statically bound and can never dispatch to an override.
/// </summary>
public static class DispatchExpansion
{
    public static async Task<IReadOnlyList<DerivedCallerEdge>> CollectDerivedAsync(
        IIndexRepository repository,
        string walkIndexId,
        string incomingIndexId,
        string startSymbolId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(walkIndexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingIndexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(startSymbolId);

        var paths = await CollectSlotPathsAsync(repository, walkIndexId, startSymbolId, cancellationToken);
        if (paths.Count == 0)
            return [];
        var incomingBySlot = await CollectSlotIncomingAsync(repository, incomingIndexId, paths, cancellationToken);
        if (incomingBySlot.Count == 0)
            return [];
        var names = await ResolveSlotNamesAsync(repository, walkIndexId, paths, cancellationToken);

        var routesByEdge = new Dictionary<string, (IndexRelationshipRecord Edge, HashSet<string> Routes)>(
            StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!incomingBySlot.TryGetValue(path[^1], out var incoming))
                continue;
            if (!TryRenderRoute(path, names, out var route))
                continue;
            foreach (var edge in incoming)
            {
                if (!routesByEdge.TryGetValue(edge.RelationshipId, out var entry))
                {
                    entry = (edge, new HashSet<string>(StringComparer.Ordinal));
                    routesByEdge[edge.RelationshipId] = entry;
                }

                entry.Routes.Add(route);
            }
        }

        return routesByEdge.Values
            .Select(item => new DerivedCallerEdge(item.Edge, item.Routes.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    public static MergedCallerPage MergeAndTake(
        IReadOnlyList<RelationshipQueryResult> exact,
        IReadOnlyList<RelationshipQueryResult> derived,
        int limit,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(derived);

        var exactIds = new HashSet<string>(exact.Select(edge => edge.RelationshipId), StringComparer.Ordinal);
        var deduped = derived
            .Where(row => !exactIds.Contains(row.RelationshipId))
            .GroupBy(row => row.RelationshipId, StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                Routes = group
                    .SelectMany(row => row.Routes ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            })
            .OrderBy(CallerIdentity, StringComparer.Ordinal)
            .ThenBy(row => row.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        var orderedExact = exact
            .OrderBy(CallerIdentity, StringComparer.Ordinal)
            .ThenBy(row => row.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        return new MergedCallerPage(
            orderedExact.Concat(deduped).Skip(offset).Take(limit).ToArray(),
            orderedExact.Length,
            deduped.Length);
    }

    public static string CallerIdentity(RelationshipQueryResult edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Source.QualifiedName ?? edge.Source.RawText ?? edge.Source.SymbolId ?? string.Empty;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<string>>> CollectSlotPathsAsync(
        IIndexRepository repository,
        string indexId,
        string startSymbolId,
        CancellationToken cancellationToken)
    {
        var paths = new List<IReadOnlyList<string>>();
        var stack = new Stack<(string SymbolId, List<string> Path, HashSet<string> Seen)>();
        stack.Push((startSymbolId, [], new HashSet<string>([startSymbolId], StringComparer.Ordinal)));
        while (stack.Count > 0)
        {
            var (current, path, seen) = stack.Pop();
            var outgoing = await repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
                indexId, current, cancellationToken);
            foreach (var edge in outgoing)
            {
                if (!IsSlotKind(edge.Kind) || edge.TargetSymbolId is null || seen.Contains(edge.TargetSymbolId))
                    continue;
                var next = new List<string>(path) { edge.TargetSymbolId };
                paths.Add(next);
                var nextSeen = new HashSet<string>(seen, StringComparer.Ordinal) { edge.TargetSymbolId };
                stack.Push((edge.TargetSymbolId, next, nextSeen));
            }
        }

        return paths;
    }

    private static async Task<Dictionary<string, IReadOnlyList<IndexRelationshipRecord>>> CollectSlotIncomingAsync(
        IIndexRepository repository,
        string indexId,
        IReadOnlyList<IReadOnlyList<string>> paths,
        CancellationToken cancellationToken)
    {
        var incomingBySlot = new Dictionary<string, IReadOnlyList<IndexRelationshipRecord>>(StringComparer.Ordinal);
        foreach (var slotId in paths.Select(path => path[^1]).Distinct(StringComparer.Ordinal))
        {
            var incoming = await repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                indexId, slotId, nameof(RelationshipKind.CallsVirtual), int.MaxValue, cancellationToken);
            if (incoming.Count > 0)
                incomingBySlot[slotId] = incoming;
        }

        return incomingBySlot;
    }

    private static async Task<Dictionary<string, string>> ResolveSlotNamesAsync(
        IIndexRepository repository,
        string indexId,
        IReadOnlyList<IReadOnlyList<string>> paths,
        CancellationToken cancellationToken)
    {
        var slotIds = paths.SelectMany(path => path).Distinct(StringComparer.Ordinal).ToArray();
        var symbols = await repository.GetCompletedSymbolsByIdsAsync(indexId, slotIds, cancellationToken);
        return symbols.ToDictionary(symbol => symbol.SymbolId, symbol => symbol.QualifiedName, StringComparer.Ordinal);
    }

    private static bool TryRenderRoute(
        IReadOnlyList<string> path,
        Dictionary<string, string> names,
        out string route)
    {
        var slots = new string[path.Count];
        for (var index = 0; index < path.Count; index++)
        {
            if (!names.TryGetValue(path[index], out var name))
            {
                route = string.Empty;
                return false;
            }

            slots[index] = name;
        }

        route = "via " + string.Join(", ", slots);
        return true;
    }

    private static bool IsSlotKind(string kind) =>
        string.Equals(kind, nameof(RelationshipKind.Overrides), StringComparison.Ordinal)
            || string.Equals(kind, nameof(RelationshipKind.ImplementsMethod), StringComparison.Ordinal);
}
