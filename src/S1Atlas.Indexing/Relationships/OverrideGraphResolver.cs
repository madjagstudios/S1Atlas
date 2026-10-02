using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Relationships;

public sealed class OverrideGraphResolver
{
    public IReadOnlyList<RelationshipFact> Resolve(
        IReadOnlyList<ManagedTypeFacts> types,
        CodebaseKind codebase,
        CodeChannel channel,
        IReadOnlyDictionary<string, string> knownMembers)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(knownMembers);
        var byFullName = types.ToDictionary(type => type.FullName, StringComparer.Ordinal);
        var result = new List<RelationshipFact>();
        var emitted = new HashSet<(string Source, string Target, RelationshipKind Kind)>();
        foreach (var type in types)
        {
            if (type.IsInterface)
                continue;
            foreach (var member in type.Members)
            {
                if (member.Kind != ManagedMemberKind.Method)
                    continue;
                var sourceName = ManagedMemberIdentity.Render(type.FullName, member);
                var sourceKey = SymbolIdentity.Create(codebase, channel, SymbolKind.Method, sourceName).CanonicalKey;
                foreach (var declaration in member.MethodImplDeclarationsOrEmpty)
                {
                    var kind = DeclaresInterface(type, DeclaringTypeOf(declaration))
                        ? RelationshipKind.ImplementsMethod
                        : RelationshipKind.Overrides;
                    Emit(result, emitted, sourceKey, declaration, kind, knownMembers);
                }

                if (member.IsVirtual && !member.IsNewSlot && FindBaseSlotTarget(type, member, byFullName) is { } slot)
                    Emit(result, emitted, sourceKey, slot, RelationshipKind.Overrides, knownMembers);
            }

            foreach (var @interface in type.Interfaces)
            {
                if (byFullName.GetValueOrDefault(OpenDefinitionName(@interface)) is not { } interfaceType)
                    continue;
                var substituted = !string.Equals(@interface, interfaceType.FullName, StringComparison.Ordinal);
                foreach (var slot in interfaceType.Members)
                {
                    if (slot.Kind != ManagedMemberKind.Method)
                        continue;
                    if (FindImplementer(type, slot, substituted, byFullName) is not { } implementer)
                        continue;
                    var sourceName = ManagedMemberIdentity.Render(implementer.Type.FullName, implementer.Member);
                    var sourceKey = SymbolIdentity.Create(codebase, channel, SymbolKind.Method, sourceName).CanonicalKey;
                    Emit(
                        result,
                        emitted,
                        sourceKey,
                        ManagedMemberIdentity.Render(interfaceType.FullName, slot),
                        RelationshipKind.ImplementsMethod,
                        knownMembers);
                }
            }
        }

        return result;
    }

    private static string? FindBaseSlotTarget(
        ManagedTypeFacts type,
        ManagedMemberFacts member,
        IReadOnlyDictionary<string, ManagedTypeFacts> byFullName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = type.BaseType;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            var open = OpenDefinitionName(current);
            if (!byFullName.TryGetValue(open, out var baseType))
            {
                return CanonicalSignatureRenderer.RenderMethod(
                    open,
                    member.Name,
                    member.ReturnType ?? "System.Void",
                    member.ParameterTypesOrEmpty,
                    member.GenericParameterCount);
            }

            if (MatchMember(baseType.Members, member, !string.Equals(current, open, StringComparison.Ordinal)) is { } slot)
                return ManagedMemberIdentity.Render(baseType.FullName, slot);
            current = baseType.BaseType;
        }

        return null;
    }

    private static (ManagedTypeFacts Type, ManagedMemberFacts Member)? FindImplementer(
        ManagedTypeFacts type,
        ManagedMemberFacts slot,
        bool substituted,
        IReadOnlyDictionary<string, ManagedTypeFacts> byFullName)
    {
        if (MatchMember(type.Members, slot, substituted) is { } declared)
            return (type, declared);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = type.BaseType;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            if (!byFullName.TryGetValue(OpenDefinitionName(current), out var baseType))
                return null;
            if (MatchMember(baseType.Members, slot, substituted) is { } inherited)
                return (baseType, inherited);
            current = baseType.BaseType;
        }

        return null;
    }

    private static ManagedMemberFacts? MatchMember(
        IReadOnlyList<ManagedMemberFacts> candidates,
        ManagedMemberFacts member,
        bool allowSubstitutedMatch)
    {
        ManagedMemberFacts? fallback = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != ManagedMemberKind.Method
                || !string.Equals(candidate.Name, member.Name, StringComparison.Ordinal)
                || candidate.GenericParameterCount != member.GenericParameterCount
                || candidate.ParameterTypesOrEmpty.Count != member.ParameterTypesOrEmpty.Count)
            {
                continue;
            }

            if (candidate.ParameterTypesOrEmpty.SequenceEqual(member.ParameterTypesOrEmpty, StringComparer.Ordinal))
                return candidate;
            fallback ??= allowSubstitutedMatch ? candidate : null;
            if (fallback is not null && !ReferenceEquals(fallback, candidate))
                return null;
        }

        return fallback;
    }

    private static bool DeclaresInterface(ManagedTypeFacts type, string declaringType)
    {
        var open = OpenDefinitionName(declaringType);
        return type.Interfaces.Any(@interface =>
            string.Equals(OpenDefinitionName(@interface), open, StringComparison.Ordinal));
    }

    private static string DeclaringTypeOf(string declaration)
    {
        var separator = declaration.IndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? declaration : declaration.Substring(0, separator);
    }

    private static string OpenDefinitionName(string name)
    {
        var current = name;
        while (true)
        {
            var start = current.LastIndexOf('<');
            if (start < 0)
                return current;
            var end = current.IndexOf('>', start);
            if (end < 0)
                return current;
            current = current.Remove(start, end - start + 1);
        }
    }

    private static void Emit(
        List<RelationshipFact> result,
        HashSet<(string Source, string Target, RelationshipKind Kind)> emitted,
        string sourceKey,
        string target,
        RelationshipKind kind,
        IReadOnlyDictionary<string, string> knownMembers)
    {
        if (!emitted.Add((sourceKey, target, kind)))
            return;
        result.Add(new RelationshipFact(
            sourceKey,
            knownMembers.GetValueOrDefault(target),
            target,
            kind,
            RelationshipEvidence.Metadata));
    }

}
