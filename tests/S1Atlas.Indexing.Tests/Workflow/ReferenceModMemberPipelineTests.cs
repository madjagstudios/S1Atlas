using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class ReferenceModMemberPipelineTests
{
    [Fact]
    public async Task Compiled_reference_field_reads_writes_and_property_accessors_resolve_to_persisted_game_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-members-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dataRoot = Path.Combine(root, "atlas");
            Directory.CreateDirectory(dataRoot);
            var repository = new SqliteAtlasRepository(Path.Combine(dataRoot, "atlas.db"), Path.Combine(dataRoot, "backups"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var seed = await HarmonyPatchAtlas.SeedAsync(repository, dataRoot, TestContext.Current.CancellationToken);

            var gameSymbols = await repository.GetCompletedSymbolsAsync(seed.GameIndexId, TestContext.Current.CancellationToken);
            var referenceSymbols = await repository.GetCompletedSymbolsAsync(seed.ReferenceIndexId, TestContext.Current.CancellationToken);
            var relationships = await repository.GetCompletedRelationshipsAsync(seed.ReferenceIndexId, TestContext.Current.CancellationToken);
            var source = Assert.Single(referenceSymbols, symbol =>
                symbol.Kind == "Method" &&
                symbol.QualifiedName == seed.ModId + "/Mod.MemberAccessProbe::ReadWrite(Game.MemberState,Game.OtherMemberState):System.Int32");
            Assert.Equal(seed.ReferenceSnapshotId, source.SnapshotId);
            var edges = relationships.Where(edge => edge.SourceSymbolId == source.SymbolId).ToArray();

            var members = new[] { "Game.MemberState", "Game.OtherMemberState" }
                .Select(typeName => (
                    TypeName: typeName,
                    Count: Assert.Single(gameSymbols, symbol => symbol.Kind == "Field" && symbol.QualifiedName == typeName + "::System.Int32 Count"),
                    SharedCount: Assert.Single(gameSymbols, symbol => symbol.Kind == "Field" && symbol.QualifiedName == typeName + "::System.Int32 SharedCount"),
                    Property: Assert.Single(gameSymbols, symbol => symbol.Kind == "Property" && symbol.QualifiedName == typeName + "::System.Int32 Value")))
                .ToArray();

            Assert.Equal(members[0].Count.Signature, members[1].Count.Signature);
            Assert.NotEqual(members[0].Count.SymbolId, members[1].Count.SymbolId);
            Assert.Equal(members[0].SharedCount.Signature, members[1].SharedCount.Signature);
            Assert.NotEqual(members[0].SharedCount.SymbolId, members[1].SharedCount.SymbolId);
            Assert.Equal(members[0].Property.Signature, members[1].Property.Signature);
            Assert.NotEqual(members[0].Property.SymbolId, members[1].Property.SymbolId);

            foreach (var (typeName, count, sharedCount, property) in members)
            {
                Assert.Equal("System.Int32 Count", count.Signature);
                Assert.Equal(typeName + "::System.Int32 Count", count.QualifiedName);
                Assert.Equal("System.Int32 SharedCount", sharedCount.Signature);
                Assert.Equal(typeName + "::System.Int32 SharedCount", sharedCount.QualifiedName);
                Assert.Equal("System.Int32 Value", property.Signature);
                Assert.Equal(typeName + "::System.Int32 Value", property.QualifiedName);
                Assert.Equal(seed.GameSnapshotId, property.SnapshotId);

                foreach (var field in new[] { count, sharedCount })
                {
                    Assert.Equal(seed.GameSnapshotId, field.SnapshotId);
                    Assert.DoesNotContain(referenceSymbols, symbol => symbol.SymbolId == field.SymbolId);
                    foreach (var kind in new[] { "ReadsField", "WritesField" })
                    {
                        var edge = Assert.Single(edges, edge => edge.Kind == kind && edge.TargetSymbolId == field.SymbolId);
                        Assert.Equal("RecoveredIL", edge.Evidence);
                        Assert.Equal(field.QualifiedName, edge.TargetText);
                        Assert.Equal(seed.ReferenceSnapshotId, edge.SnapshotId);
                    }
                }

                foreach (var accessor in new[] { "get_Value():System.Int32", "set_Value(System.Int32):System.Void" })
                {
                    var qualifiedName = typeName + "::" + accessor;
                    var method = Assert.Single(gameSymbols, symbol => symbol.Kind == "Method" && symbol.QualifiedName == qualifiedName);
                    Assert.Equal(seed.GameSnapshotId, method.SnapshotId);
                    Assert.DoesNotContain(referenceSymbols, symbol => symbol.SymbolId == method.SymbolId);
                    var edge = Assert.Single(edges, edge =>
                        edge.Kind is "Calls" or "CallsVirtual" && edge.TargetSymbolId == method.SymbolId);
                    Assert.Equal("RecoveredIL", edge.Evidence);
                    Assert.Equal(qualifiedName, edge.TargetText);
                    Assert.Equal(seed.ReferenceSnapshotId, edge.SnapshotId);
                }
            }
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }
}
