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
        foreach (var type in decompilation.Types)
        {
            var source = SymbolIdentity.Create(codebase, channel, SymbolKind.Type, type.FullName).CanonicalKey;
            if (!string.IsNullOrWhiteSpace(type.BaseType) && type.BaseType != "System.Object")
                result.Add(Metadata(source, RelationshipKind.Inherits, type.BaseType));
            foreach (var @interface in type.Interfaces)
                result.Add(Metadata(source, RelationshipKind.ImplementsInterface, @interface));

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
                var memberKey = SymbolIdentity.Create(codebase, channel, memberKind, type.FullName + "::" + member.Signature).CanonicalKey;
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
                    result.Add(new RelationshipFact(memberKey, null, reference.Target, kind, RelationshipEvidence.RecoveredIL));
                }
            }
        }
        return result;
    }

    private static RelationshipFact Metadata(string source, RelationshipKind kind, string target) =>
        new(source, null, target, kind, RelationshipEvidence.Metadata);
}
