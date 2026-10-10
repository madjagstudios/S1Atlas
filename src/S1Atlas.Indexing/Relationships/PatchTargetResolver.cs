using S1Atlas.Core;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Workflow;

namespace S1Atlas.Indexing.Relationships;

public static class PatchTargetResolver
{
    public const string GameOrigin = "game";

    public static IndexRelationshipRecord ResolvePatchEdge(
        ManagedPatchFact fact,
        IndexSymbolRecord hostSource,
        string modId,
        IReadOnlyDictionary<(string Type, string Name), List<IndexSymbolRecord>> gameMembers,
        IReadOnlySet<string> gameTypes,
        IReadOnlyDictionary<(string Origin, string Type, string Name), List<IndexSymbolRecord>> modMembers)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(hostSource);
        ArgumentNullException.ThrowIfNull(gameTypes);
        var source = ResolveSource(fact, hostSource, modId, modMembers);

        var kindName = fact.Kind.ToString();
        if (fact.Reason is not null)
            return ResolveUnresolved(fact, source, kindName);

        if (fact.TargetSignature is null ||
            !SymbolNames.TrySplitMember(fact.TargetSignature, out var typeName, out var memberTail) ||
            !TryParseMemberTail(memberTail, out var memberName, out var parameters))
        {
            return UnresolvedEdge(
                source,
                fact,
                kindName,
                HarmonyPatchReasons.UnparsableTarget,
                fact.TargetSignature ?? source.Signature);
        }

        var normalizedType = InteropTypeNames.Normalize(typeName);
        if (!gameTypes.Contains(normalizedType))
        {
            return UnresolvedEdge(
                source,
                fact,
                kindName,
                HarmonyPatchReasons.TargetTypeNotFound,
                fact.TargetSignature);
        }

        if (!gameMembers.TryGetValue((normalizedType, memberName), out var candidates))
        {
            return UnresolvedEdge(
                source,
                fact,
                kindName,
                HarmonyPatchReasons.TargetMemberNotFound,
                fact.TargetSignature);
        }

        var matching = candidates
            .DistinctBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .Where(symbol => parameters is null || MatchesParameters(symbol.Signature, parameters))
            .ToArray();
        if (matching.Length == 0)
        {
            return UnresolvedEdge(
                source,
                fact,
                kindName,
                HarmonyPatchReasons.NoMatchingOverload,
                fact.TargetSignature);
        }

        if (matching.Length > 1)
            return UnresolvedEdge(
                source,
                fact,
                kindName,
                HarmonyPatchReasons.AmbiguousOverload,
                fact.TargetSignature);

        var target = matching[0];
        return new IndexRelationshipRecord(
            IndexingWorkflow.HashId(source.SymbolId + "\n" + RelationshipKind.Patches + "\n" + kindName + "\n" + target.SymbolId),
            source.SnapshotId,
            source.SymbolId,
            target.SymbolId,
            fact.TargetSignature,
            RelationshipKind.Patches.ToString(),
            fact.Evidence.ToString(),
            null,
            kindName);
    }

    private static IndexSymbolRecord ResolveSource(
        ManagedPatchFact fact,
        IndexSymbolRecord hostSource,
        string modId,
        IReadOnlyDictionary<(string Origin, string Type, string Name), List<IndexSymbolRecord>> modMembers)
    {
        // Attribute patches patch from their own member. A manual patch hangs on its patch
        // method when that is identified uniquely, and otherwise on the method that
        // registers it, so the patch is reported at the caller instead of dropped.
        if (fact.Evidence != RelationshipEvidence.RecoveredIL ||
            fact.PatchMethodType is null || fact.PatchMethodName is null ||
            !modMembers.TryGetValue((modId, fact.PatchMethodType, fact.PatchMethodName), out var candidates))
            return hostSource;

        var matching = candidates
            .DistinctBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .Where(symbol => fact.PatchMethodArgumentTypes is null || MatchesParameters(symbol.Signature, fact.PatchMethodArgumentTypes))
            .ToArray();
        return matching.Length == 1 ? matching[0] : hostSource;
    }

    private static IndexRelationshipRecord ResolveUnresolved(ManagedPatchFact fact, IndexSymbolRecord source, string kindName)
    {
        // Every reason the readers emit is kept: a recognised patch with an unresolvable
        // target is reported, never dropped. Only the NonConstantArguments marker is
        // translated, into the stored non-constant-target reason.
        var reason = string.Equals(fact.Reason, HarmonyPatchReasons.NonConstantArguments, StringComparison.Ordinal)
            ? HarmonyPatchReasons.NonConstantTarget
            : fact.Reason!;
        return UnresolvedEdge(source, fact, kindName, reason, fact.TargetSignature ?? source.Signature);
    }

    private static IndexRelationshipRecord UnresolvedEdge(
        IndexSymbolRecord source,
        ManagedPatchFact fact,
        string kindName,
        string reason,
        string text) =>
        new(
            IndexingWorkflow.HashId(source.SymbolId + "\n" + RelationshipKind.Patches + "\n" + kindName + "\n" + text),
            source.SnapshotId,
            source.SymbolId,
            null,
            HarmonyPatchReasons.Qualify(reason, text),
            RelationshipKind.Patches.ToString(),
            fact.Evidence.ToString(),
            null,
            kindName);

    private static bool MatchesParameters(string signature, IReadOnlyList<string> parameters)
    {
        if (!SymbolNames.TrySplitMember(signature, out _, out var memberTail))
            return false;
        if (!TryParseMemberTail(memberTail, out _, out var candidateParameters) || candidateParameters is null)
            return false;
        if (candidateParameters.Count != parameters.Count)
            return false;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!string.Equals(
                    InteropTypeNames.Normalize(candidateParameters[i]),
                    InteropTypeNames.Normalize(parameters[i]),
                    StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool TryParseMemberTail(string tail, out string memberName, out IReadOnlyList<string>? parameters)
    {
        memberName = string.Empty;
        parameters = null;
        var open = tail.IndexOf('(');
        if (open < 0)
        {
            var end = tail.IndexOfAny([':', ' ']);
            memberName = end < 0 ? tail : tail[..end];
            return memberName.Length > 0;
        }

        memberName = tail[..open];
        if (memberName.Length == 0 || memberName.IndexOfAny([':', ' ']) >= 0)
            return false;

        var depth = 0;
        var close = -1;
        for (var i = open; i < tail.Length; i++)
        {
            if (tail[i] == '(')
                depth++;
            else if (tail[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0)
            return false;
        var inner = tail[(open + 1)..close];
        if (inner.Length == 0)
        {
            parameters = [];
            return true;
        }

        parameters = SplitTopLevel(inner);
        return parameters is not null;
    }

    private static IReadOnlyList<string>? SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var current = inner[i];
            if (current is '<' or '(' or '[')
                depth++;
            else if (current is '>' or ')' or ']')
                depth--;
            else if (current == ',' && depth == 0)
            {
                var part = inner[start..i].Trim();
                if (part.Length == 0)
                    return null;
                parts.Add(part);
                start = i + 1;
            }
        }

        var last = inner[start..].Trim();
        if (last.Length == 0)
            return null;
        parts.Add(last);
        return parts;
    }
}
