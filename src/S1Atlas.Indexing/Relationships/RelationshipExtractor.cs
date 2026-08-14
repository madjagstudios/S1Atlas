using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Relationships;

public sealed class RelationshipExtractor
{
    public IReadOnlyList<RelationshipFact> Extract(
        ManagedDecompilation decompilation,
        CodebaseKind codebase,
        CodeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(decompilation);
        var result = new List<RelationshipFact>();
        var knownTypes = decompilation.Types
            .Select(type => (type.FullName, Key: SymbolIdentity.Create(codebase, channel, SymbolKind.Type, type.FullName).CanonicalKey))
            .ToDictionary(item => item.FullName, item => item.Key, StringComparer.Ordinal);
        var knownMembers = decompilation.Types
            .SelectMany(type => type.Members.Select(member =>
                (Name: ManagedMemberIdentity.Render(type.FullName, member), Kind: ToSymbolKind(member.Kind))))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => SymbolIdentity.Create(codebase, channel, group.First().Kind, group.Key).CanonicalKey, StringComparer.Ordinal);
        foreach (var type in decompilation.Types)
        {
            var source = SymbolIdentity.Create(codebase, channel, SymbolKind.Type, type.FullName).CanonicalKey;
            if (!string.IsNullOrWhiteSpace(type.BaseType) && type.BaseType != "System.Object")
                result.Add(Metadata(source, RelationshipKind.Inherits, type.BaseType, knownTypes));
            foreach (var @interface in type.Interfaces)
                result.Add(Metadata(source, RelationshipKind.ImplementsInterface, @interface, knownTypes));

            foreach (var member in type.Members)
            {
                var memberKind = member.Kind switch
                {
                    ManagedMemberKind.Constructor => SymbolKind.Constructor,
                    ManagedMemberKind.Method => SymbolKind.Method,
                    ManagedMemberKind.Field => SymbolKind.Field,
                    ManagedMemberKind.Property => SymbolKind.Property,
                    ManagedMemberKind.Event => SymbolKind.Event,
                    _ => throw new ArgumentOutOfRangeException()
                };
                var memberName = ManagedMemberIdentity.Render(type.FullName, member);
                var memberKey = SymbolIdentity.Create(codebase, channel, memberKind, memberName).CanonicalKey;
                if (!string.IsNullOrWhiteSpace(member.ValueType))
                {
                    var relationshipKind = member.Kind switch
                    {
                        ManagedMemberKind.Field => RelationshipKind.FieldType,
                        ManagedMemberKind.Property => RelationshipKind.PropertyType,
                        ManagedMemberKind.Event => RelationshipKind.EventType,
                        _ => (RelationshipKind?)null
                    };
                    if (relationshipKind is not null)
                        result.Add(Metadata(memberKey, relationshipKind.Value, CanonicalSignatureRenderer.RenderType(member.ValueType), knownTypes));
                }
                foreach (var parameterType in member.ParameterTypesOrEmpty)
                    result.Add(Metadata(memberKey, RelationshipKind.ParameterType, CanonicalSignatureRenderer.RenderType(parameterType), knownTypes));
                if (!string.IsNullOrWhiteSpace(member.ReturnType) &&
                    !string.Equals(CanonicalSignatureRenderer.RenderType(member.ReturnType), "System.Void", StringComparison.Ordinal))
                    result.Add(Metadata(memberKey, RelationshipKind.ReturnType, CanonicalSignatureRenderer.RenderType(member.ReturnType), knownTypes));
                foreach (var reference in member.References)
                {
                    var kind = reference.Kind switch
                    {
                        ManagedReferenceKind.Calls => RelationshipKind.Calls,
                        ManagedReferenceKind.Constructs => RelationshipKind.Constructs,
                        ManagedReferenceKind.ReadsField => RelationshipKind.ReadsField,
                        ManagedReferenceKind.WritesField => RelationshipKind.WritesField,
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    result.Add(new RelationshipFact(memberKey, knownMembers.GetValueOrDefault(reference.Target), reference.Target, kind, RelationshipEvidence.RecoveredIL));
                }
            }
        }
        return result;
    }

    private static RelationshipFact Metadata(
        string source,
        RelationshipKind kind,
        string target,
        IReadOnlyDictionary<string, string> knownTypes) =>
        new(source, knownTypes.GetValueOrDefault(target), target, kind, RelationshipEvidence.Metadata);

    private static SymbolKind ToSymbolKind(ManagedMemberKind kind) => kind switch
    {
        ManagedMemberKind.Constructor => SymbolKind.Constructor,
        ManagedMemberKind.Method => SymbolKind.Method,
        ManagedMemberKind.Field => SymbolKind.Field,
        ManagedMemberKind.Property => SymbolKind.Property,
        ManagedMemberKind.Event => SymbolKind.Event,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
