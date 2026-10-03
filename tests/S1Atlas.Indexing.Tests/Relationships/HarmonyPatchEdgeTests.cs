using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships;

public sealed class HarmonyPatchEdgeTests
{
    [Fact]
    public async Task Reference_workflow_indexes_resolved_and_unresolved_patch_edges()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-harmony-edges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dataRoot = Path.Combine(root, "atlas");
            Directory.CreateDirectory(dataRoot);
            var repository = new SqliteAtlasRepository(Path.Combine(dataRoot, "atlas.db"), Path.Combine(dataRoot, "backups"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var seed = await HarmonyPatchAtlas.SeedAsync(repository, dataRoot, TestContext.Current.CancellationToken);

            var edges = (await repository.GetCompletedRelationshipsAsync(seed.ReferenceIndexId, TestContext.Current.CancellationToken))
                .Where(edge => edge.Kind == "Patches")
                .ToArray();

            Assert.Equal(17, edges.Count(edge => edge.TargetSymbolId is not null));
            Assert.Equal(7, edges.Count(edge => edge.TargetSymbolId is null));

            var gameSymbols = (await repository.GetCompletedSymbolsAsync(seed.GameIndexId, TestContext.Current.CancellationToken))
                .ToDictionary(symbol => symbol.SymbolId, symbol => symbol.Signature, StringComparer.Ordinal);

            Assert.Equal(7, edges.Count(edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal)));
            Assert.All(
                edges.Where(edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal)),
                edge => Assert.Equal("Prefix", edge.GeneratedDetail));
            Assert.Equal(3, edges.Count(edge =>
                edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                edge.Evidence == "Metadata"));
            Assert.Equal(4, edges.Count(edge =>
                edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                edge.Evidence == "RecoveredIL"));

            Assert.Equal(2, edges.Count(edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Compute(System.Int32,System.String)", StringComparison.Ordinal)));
            Assert.All(
                edges.Where(edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Compute(System.Int32,System.String)", StringComparison.Ordinal)),
                edge => Assert.Equal("Postfix", edge.GeneratedDetail));

            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::get_Name(", StringComparison.Ordinal));
            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::set_Name(", StringComparison.Ordinal));
            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::.ctor(", StringComparison.Ordinal));
            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::.cctor(", StringComparison.Ordinal));
            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Compute(System.Int32&)", StringComparison.Ordinal));
            Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Consume(System.Int32&)", StringComparison.Ordinal));
            var untouched = Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Untouched(", StringComparison.Ordinal));
            Assert.Equal("Prefix", untouched.GeneratedDetail);
            Assert.Equal("Metadata", untouched.Evidence);
            var transpiler = Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("Widget+Nested::Inner(", StringComparison.Ordinal));
            Assert.Equal("Transpiler", transpiler.GeneratedDetail);

            var unresolvedReasons = edges
                .Where(edge => edge.TargetSymbolId is null)
                .Select(edge => edge.TargetText!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(7, unresolvedReasons.Length);
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:ambiguous-overload:", StringComparison.Ordinal));
            Assert.Equal(2, unresolvedReasons.Count(text => text.StartsWith("unresolved:runtime-computed-target:", StringComparison.Ordinal)));
            Assert.Equal(2, unresolvedReasons.Count(text => text.StartsWith("unresolved:non-constant-target:", StringComparison.Ordinal)));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:unsupported-method-type:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:no-target-specified:", StringComparison.Ordinal));
            Assert.DoesNotContain(unresolvedReasons, text => !text.StartsWith("unresolved:", StringComparison.Ordinal));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

}
