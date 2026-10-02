using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Relationships;

public sealed record ReferenceModDecompilation(string ModId, ManagedDecompilation Decompilation);

public sealed class ReferenceRelationshipResolver
{
    public static (string Origin, string Type, string Name, int Arity, string Signature) CreateLookupKey(string origin, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var identity = CreateIdentityLookupKey(signature);
        return (origin, identity.Type, identity.Name, identity.Arity, identity.Signature);
    }

    private static string SourceKeyFor(ManagedMemberKind kind, string signature)
    {
        var symbolKind = kind switch
        {
            ManagedMemberKind.Constructor => SymbolKind.Constructor,
            ManagedMemberKind.Method => SymbolKind.Method,
            ManagedMemberKind.Field => SymbolKind.Field,
            ManagedMemberKind.Property => SymbolKind.Property,
            ManagedMemberKind.Event => SymbolKind.Event,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return SymbolIdentity.Create(CodebaseKind.ReferenceMod, CodeChannel.Installed, symbolKind, signature).CanonicalKey;
    }

    private static string RenderedNameOf(string canonicalKey)
    {
        var index = -1;
        for (var part = 0; part < 3; part++)
        {
            index = canonicalKey.IndexOf(':', index + 1);
            if (index < 0)
                return canonicalKey;
        }

        return canonicalKey[(index + 1)..];
    }

    private static (string Type, string Name, int Arity, string Signature) CreateIdentityLookupKey(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var separator = signature.IndexOf("::", StringComparison.Ordinal);
        if (separator < 1)
            return (string.Empty, signature, 0, signature);

        var type = signature[..separator];
        var member = signature[(separator + 2)..];
        var nameEnd = member.IndexOfAny(['(', ' ']);
        var name = nameEnd < 0 ? member : member[..nameEnd];
        var tick = name.LastIndexOf('`');
        var arity = tick >= 0 && int.TryParse(name[(tick + 1)..], out var parsedArity) ? parsedArity : 0;
        return (type, name, arity, signature);
    }

    public IReadOnlyList<IndexRelationshipRecord> Resolve(
        IReadOnlyList<ReferenceModDecompilation> mods,
        IReadOnlyDictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord> symbols)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(symbols);
        var targetLookup = new Dictionary<(string Type, string Name, int Arity, string Signature), List<IndexSymbolRecord>>();
        foreach (var pair in symbols)
        {
            var targetKey = (pair.Key.Type, pair.Key.Name, pair.Key.Arity, pair.Key.Signature);
            if (!targetLookup.TryGetValue(targetKey, out var candidates))
            {
                candidates = [];
                targetLookup[targetKey] = candidates;
            }

            candidates.Add(pair.Value);
        }

        var result = new List<IndexRelationshipRecord>();
        foreach (var mod in mods)
        {
            var credit = GeneratedBodyResolver.ResolveAll(mod.Decompilation, CodebaseKind.ReferenceMod, CodeChannel.Installed);
            foreach (var type in mod.Decompilation.Types)
            {
                foreach (var member in type.Members)
                {
                    var sourceSignature = ManagedMemberIdentity.Render(type.FullName, member);
                    if (!symbols.TryGetValue(CreateLookupKey(mod.ModId, sourceSignature), out var source))
                        continue;
                    var mapping = credit.GetValueOrDefault(SourceKeyFor(member.Kind, sourceSignature));
                    var credited = source;
                    string? rawSourceId = null;
                    if (mapping?.DeclaringKey is not null
                        && symbols.TryGetValue(CreateLookupKey(mod.ModId, RenderedNameOf(mapping.DeclaringKey)), out var declaring)
                        && !string.Equals(declaring.SymbolId, source.SymbolId, StringComparison.Ordinal))
                    {
                        credited = declaring;
                        rawSourceId = source.SymbolId;
                    }

                    var detail = rawSourceId is not null || mapping?.DeclaringKey is null ? mapping?.Detail : null;

                    foreach (var reference in member.References)
                    {
                        var kind = reference.Kind switch
                        {
                            ManagedReferenceKind.Calls => RelationshipKind.Calls,
                            ManagedReferenceKind.CallsVirtual => RelationshipKind.CallsVirtual,
                            ManagedReferenceKind.Constructs => RelationshipKind.Constructs,
                            ManagedReferenceKind.ReadsField => RelationshipKind.ReadsField,
                            ManagedReferenceKind.WritesField => RelationshipKind.WritesField,
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        var targetKey = CreateIdentityLookupKey(reference.Target);
                        var candidates = targetLookup.TryGetValue(targetKey, out var matchingSymbols)
                            ? matchingSymbols.DistinctBy(symbol => symbol.SymbolId, StringComparer.Ordinal).ToArray()
                            : [];
                        var target = candidates.Length == 1 ? candidates[0] : null;
                        result.Add(new IndexRelationshipRecord(
                            Indexing.Workflow.IndexingWorkflow.HashId((rawSourceId ?? credited.SymbolId) + "\n" + kind + "\n" + reference.Target),
                            credited.SnapshotId,
                            credited.SymbolId,
                            target?.SymbolId,
                            reference.Target,
                            kind.ToString(),
                            RelationshipEvidence.RecoveredIL.ToString(),
                            rawSourceId,
                            detail));
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
