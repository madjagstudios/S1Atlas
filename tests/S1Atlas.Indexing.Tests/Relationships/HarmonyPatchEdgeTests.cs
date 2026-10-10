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

            Assert.Equal(24, edges.Count(edge => edge.TargetSymbolId is not null));
            Assert.Equal(15, edges.Count(edge => edge.TargetSymbolId is null));

            var gameSymbols = (await repository.GetCompletedSymbolsAsync(seed.GameIndexId, TestContext.Current.CancellationToken))
                .ToDictionary(symbol => symbol.SymbolId, symbol => symbol.Signature, StringComparer.Ordinal);
            var modSymbols = (await repository.GetCompletedSymbolsAsync(seed.ReferenceIndexId, TestContext.Current.CancellationToken))
                .ToDictionary(symbol => symbol.SymbolId, symbol => symbol.Signature, StringComparer.Ordinal);

            Assert.Equal(12, edges.Count(edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal)));
            Assert.All(
                edges.Where(edge => edge.TargetSymbolId is not null &&
                    gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                    !modSymbols[edge.SourceSymbolId].Contains("ManualTypeMethodInfoPatch::InfoPostfix(", StringComparison.Ordinal)),
                edge => Assert.Equal("Prefix", edge.GeneratedDetail));
            var typeMethodInfo = Assert.Single(edges, edge =>
                edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                modSymbols[edge.SourceSymbolId].Contains("ManualTypeMethodInfoPatch::InfoPostfix(", StringComparison.Ordinal));
            Assert.Equal("Postfix", typeMethodInfo.GeneratedDetail);
            Assert.Equal("RecoveredIL", typeMethodInfo.Evidence);
            Assert.Equal(3, edges.Count(edge =>
                edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                edge.Evidence == "Metadata"));
            Assert.Equal(9, edges.Count(edge =>
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
            var untouched = Assert.Single(edges, edge => edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Untouched(", StringComparison.Ordinal) &&
                edge.Evidence == "Metadata");
            Assert.Equal("Prefix", untouched.GeneratedDetail);
            var transpiler = Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("Widget+Nested::Inner(", StringComparison.Ordinal));
            Assert.Equal("Transpiler", transpiler.GeneratedDetail);
            var interopParam = Assert.Single(edges, edge => edge.TargetSymbolId is not null && gameSymbols[edge.TargetSymbolId!].Contains("::Calibrate(", StringComparison.Ordinal));
            Assert.Equal("Prefix", interopParam.GeneratedDetail);
            Assert.Equal("Metadata", interopParam.Evidence);
            var disambiguated = Assert.Single(edges, edge =>
                edge.TargetSymbolId is not null &&
                gameSymbols[edge.TargetSymbolId!].Contains("::Run(", StringComparison.Ordinal) &&
                modSymbols[edge.SourceSymbolId].Contains("ManualOverloadDisambiguationPatch::Do(System.Int32)", StringComparison.Ordinal));
            Assert.Equal("Prefix", disambiguated.GeneratedDetail);
            Assert.Equal("RecoveredIL", disambiguated.Evidence);
            var missingType = Assert.Single(edges, edge =>
                edge.TargetSymbolId is null &&
                modSymbols[edge.SourceSymbolId].Contains("MissingTypeTargetPatch::Prefix(", StringComparison.Ordinal));
            Assert.StartsWith("unresolved:target-type-not-found:", missingType.TargetText, StringComparison.Ordinal);
            var missingMember = Assert.Single(edges, edge =>
                edge.TargetSymbolId is null &&
                modSymbols[edge.SourceSymbolId].Contains("MissingPatch::Prefix(", StringComparison.Ordinal));
            Assert.StartsWith("unresolved:target-member-not-found:", missingMember.TargetText, StringComparison.Ordinal);
            var noOverload = Assert.Single(edges, edge =>
                edge.TargetSymbolId is null &&
                modSymbols[edge.SourceSymbolId].Contains("NoOverloadPatch::Prefix(", StringComparison.Ordinal));
            Assert.StartsWith("unresolved:no-matching-overload:", noOverload.TargetText, StringComparison.Ordinal);

            // A manual patch whose patch method cannot be identified hangs on the method
            // that registers it, so its target is still checked (AT-136).
            foreach (var host in new[]
            {
                "ManualAmbiguousMethodPatch::Install(",
                "ManualUnknownMethodPatch::Install(",
                "ManualNonConstantPatchTypesPatch::Install(",
            })
            {
                var hosted = Assert.Single(edges, edge => modSymbols[edge.SourceSymbolId].Contains(host, StringComparison.Ordinal));
                Assert.Contains("::Run(", gameSymbols[hosted.TargetSymbolId!], StringComparison.Ordinal);
                Assert.Equal("Prefix", hosted.GeneratedDetail);
                Assert.Equal("RecoveredIL", hosted.Evidence);
            }

            var hostedUntouched = Assert.Single(edges, edge =>
                modSymbols[edge.SourceSymbolId].Contains("ManualNonConstantMethodPatch::Install(", StringComparison.Ordinal));
            Assert.Contains("::Untouched(", gameSymbols[hostedUntouched.TargetSymbolId!], StringComparison.Ordinal);
            Assert.Equal("Prefix", hostedUntouched.GeneratedDetail);
            Assert.Equal("RecoveredIL", hostedUntouched.Evidence);

            var unresolvedReasons = edges
                .Where(edge => edge.TargetSymbolId is null)
                .Select(edge => edge.TargetText!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(15, unresolvedReasons.Length);
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:ambiguous-overload:", StringComparison.Ordinal));
            Assert.Equal(2, unresolvedReasons.Count(text => text.StartsWith("unresolved:runtime-computed-target:", StringComparison.Ordinal)));
            Assert.Equal(3, unresolvedReasons.Count(text => text.StartsWith("unresolved:non-constant-target:", StringComparison.Ordinal)));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:unsupported-method-type:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:no-target-specified:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:target-type-not-found:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:target-member-not-found:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:no-matching-overload:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:unknown-declaring-type:", StringComparison.Ordinal));
            Assert.Contains(unresolvedReasons, text => text.StartsWith("unresolved:unknown-member-name:", StringComparison.Ordinal));
            Assert.Equal(2, unresolvedReasons.Count(text => text.StartsWith("unresolved:unrecognized-manual-shape:", StringComparison.Ordinal)));
            Assert.DoesNotContain(unresolvedReasons, text => !text.StartsWith("unresolved:", StringComparison.Ordinal));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

}
