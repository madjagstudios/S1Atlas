using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Relationships;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships;

public sealed class ReferenceRelationshipResolverTests
{
    [Fact]
    public void Builds_target_lookup_once_before_resolving_references()
    {
        var snapshotId = "reference-snapshot";
        var modSymbol = new IndexSymbolRecord("mod-run", snapshotId, "ReferenceMod:Installed:Method:qol/Mods.Entry::Run():System.Void", "Method", "qol/Mods.Entry::Run():System.Void", "Mods.Entry::Run():System.Void", false);
        var gameSymbol = new IndexSymbolRecord("existing-game-symbol", "game-snapshot", "ScheduleI:Installed:Method:Game.Target::Run():System.Void", "Method", "Game.Target::Run():System.Void", "Game.Target::Run():System.Void", false);
        var symbols = new CountingReadOnlyDictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord>(new Dictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord>
        {
            [ReferenceRelationshipResolver.CreateLookupKey("game", gameSymbol.Signature)] = gameSymbol,
            [ReferenceRelationshipResolver.CreateLookupKey("qol", modSymbol.Signature)] = modSymbol
        });
        var decompilation = new ManagedDecompilation("qol.dll", "", [new ManagedTypeFacts(
            "Mods.Entry", "Mods", "Entry", null, [], [new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Run", true, [
                new ManagedReferenceFact(ManagedReferenceKind.Calls, gameSymbol.Signature),
                new ManagedReferenceFact(ManagedReferenceKind.Calls, gameSymbol.Signature)
            ], [], "System.Void")])]);

        var relationships = new ReferenceRelationshipResolver().Resolve([new ReferenceModDecompilation("qol", decompilation)], symbols);

        Assert.Single(relationships);
        Assert.Equal(1, symbols.EnumerationCount);
    }

    [Fact]
    public void Resolves_game_targets_by_persisted_symbol_id_and_keeps_unknown_targets_unresolved()
    {
        var snapshotId = "reference-snapshot";
        var modSymbol = new IndexSymbolRecord("mod-run", snapshotId, "ReferenceMod:Installed:Method:qol/Mods.Entry::Run():System.Void", "Method", "qol/Mods.Entry::Run():System.Void", "Mods.Entry::Run():System.Void", false);
        var gameSymbol = new IndexSymbolRecord("existing-game-symbol", "game-snapshot", "ScheduleI:Installed:Method:Game.Target::Run():System.Void", "Method", "Game.Target::Run():System.Void", "Game.Target::Run():System.Void", false);
        var lookup = new Dictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord>
        {
            [ReferenceRelationshipResolver.CreateLookupKey("game", gameSymbol.Signature)] = gameSymbol,
            [ReferenceRelationshipResolver.CreateLookupKey("qol", modSymbol.Signature)] = modSymbol
        };
        var decompilation = new ManagedDecompilation("qol.dll", "", [new ManagedTypeFacts(
            "Mods.Entry", "Mods", "Entry", null, [], [new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Run", true, [
                new ManagedReferenceFact(ManagedReferenceKind.Calls, gameSymbol.Signature),
                new ManagedReferenceFact(ManagedReferenceKind.ReadsField, "Unknown.Target::System.Int32 Value")
            ], [], "System.Void")])]);

        var relationships = new ReferenceRelationshipResolver().Resolve([new ReferenceModDecompilation("qol", decompilation)], lookup);

        var gameCall = Assert.Single(relationships, relationship => relationship.Kind == "Calls");
        Assert.Equal(modSymbol.SymbolId, gameCall.SourceSymbolId);
        Assert.Equal(gameSymbol.SymbolId, gameCall.TargetSymbolId);
        var unresolved = Assert.Single(relationships, relationship => relationship.Kind == "ReadsField");
        Assert.Null(unresolved.TargetSymbolId);
        Assert.Equal("Unknown.Target::System.Int32 Value", unresolved.TargetText);
    }

    private sealed class CountingReadOnlyDictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> inner) : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        public int EnumerationCount { get; private set; }

        public TValue this[TKey key] => inner[key];

        public IEnumerable<TKey> Keys => inner.Keys;

        public IEnumerable<TValue> Values => inner.Values;

        public int Count => inner.Count;

        public bool ContainsKey(TKey key) => inner.ContainsKey(key);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            EnumerationCount++;
            return inner.GetEnumerator();
        }

        public bool TryGetValue(TKey key, out TValue value) => inner.TryGetValue(key, out value!);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
