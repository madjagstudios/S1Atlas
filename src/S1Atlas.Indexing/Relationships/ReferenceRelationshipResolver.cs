using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Relationships;

public sealed record ReferenceModDecompilation(string ModId, ManagedDecompilation Decompilation);

public sealed class ReferenceRelationshipResolver
{
    public static (string Origin, string Type, string Name, int Arity, string Signature) CreateLookupKey(string origin, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var separator = signature.IndexOf("::", StringComparison.Ordinal);
        if (separator < 1)
            return (origin, string.Empty, signature, 0, signature);

        var type = signature[..separator];
        var member = signature[(separator + 2)..];
        var nameEnd = member.IndexOfAny(['(', ' ']);
        var name = nameEnd < 0 ? member : member[..nameEnd];
        var tick = name.LastIndexOf('`');
        var arity = tick >= 0 && int.TryParse(name[(tick + 1)..], out var parsedArity) ? parsedArity : 0;
        return (origin, type, name, arity, signature);
    }

    public IReadOnlyList<IndexRelationshipRecord> Resolve(
        IReadOnlyList<ReferenceModDecompilation> mods,
        IReadOnlyDictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord> symbols)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(symbols);
        var result = new List<IndexRelationshipRecord>();
        foreach (var mod in mods)
        {
            foreach (var type in mod.Decompilation.Types)
            {
                foreach (var member in type.Members)
                {
                    var sourceSignature = ManagedMemberIdentity.Render(type.FullName, member);
                    if (!symbols.TryGetValue(CreateLookupKey(mod.ModId, sourceSignature), out var source))
                        continue;
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
                        var targetKey = CreateLookupKey("target", reference.Target);
                        var candidates = symbols
                            .Where(pair =>
                                string.Equals(pair.Key.Type, targetKey.Type, StringComparison.Ordinal) &&
                                string.Equals(pair.Key.Name, targetKey.Name, StringComparison.Ordinal) &&
                                pair.Key.Arity == targetKey.Arity &&
                                string.Equals(pair.Key.Signature, targetKey.Signature, StringComparison.Ordinal))
                            .Select(pair => pair.Value)
                            .DistinctBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
                            .ToArray();
                        var target = candidates.Length == 1 ? candidates[0] : null;
                        result.Add(new IndexRelationshipRecord(
                            Indexing.Workflow.IndexingWorkflow.HashId(source.SymbolId + "\n" + kind + "\n" + reference.Target),
                            source.SnapshotId,
                            source.SymbolId,
                            target?.SymbolId,
                            reference.Target,
                            kind.ToString(),
                            RelationshipEvidence.RecoveredIL.ToString()));
                    }
                }
            }
        }

        return result
            .GroupBy(relationship => relationship.RelationshipId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(relationship => relationship.RelationshipId, StringComparer.Ordinal)
            .ToArray();
    }
}
