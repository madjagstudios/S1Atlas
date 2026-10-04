using ICSharpCode.Decompiler.CSharp;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Relationships;
using S1Atlas.Indexing.Source;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Indexing.Tests.ReferenceMods;

public sealed class ReferenceResolverVersionTests
{
    [Fact]
    public async Task Rebuilds_legacy_completed_reference_indexes_with_unresolved_game_field_edges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-resolver-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
            await repository.InitializeAsync(cancellationToken);
            var seed = await ModCheckAtlas.SeedAsync(repository, root, cancellationToken);
            var game = await new ReferenceGameSymbolLoader(repository).LoadAsync(seed.FromIndexId, cancellationToken);
            var collection = new ReferenceCollectionDefinition(
                seed.FromBuildId,
                seed.FromIndexId,
                [new ReferenceModDefinition("resolver-fixture", "Resolver Fixture", "1.0.0", null,
                    Path.GetDirectoryName(seed.ModPath)!, "fixture-content", ["*.dll"])],
                "resolver-fixture",
                "Resolver Fixture");
            var selected = new ReferenceModFileSelector().Select(collection.Mods);
            var inputHash = await new ReferenceModInputHasher().HashAsync(selected, cancellationToken);
            var collectionHash = ReferenceModIndexWorkflow.CreateCollectionHash(collection, inputHash.CollectionContentSha256);

            // Preserve the unversioned identity used before the shared resolver fix.
            var legacyId = IndexingWorkflow.CreateIndexId(
                game.IndexId + "\n" + game.VerifiedExtractionIdentity + "\n" + collectionHash,
                "ICSharpCode.Decompiler",
                typeof(CSharpDecompiler).Assembly.GetName().Version!.ToString(),
                "reference",
                15);
            var legacySnapshotId = "reference:" + game.IndexId + ":" + legacyId;
            var decompilation = await new IlSpyManagedDecompiler().DecompileAsync(seed.ModPath, cancellationToken);
            var symbols = ReferenceModIndexWorkflow.BuildSymbols("resolver-fixture", decompilation, legacySnapshotId);

            // The old lookup keyed game fields by their unqualified Signature.
            // Every symbol row still comes from the compiled fixture and real indexer.
            var legacyLookup = new Dictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord>();
            foreach (var symbol in game.Symbols)
                legacyLookup.TryAdd(ReferenceRelationshipResolver.CreateLookupKey("game", symbol.Signature), symbol);
            foreach (var symbol in symbols)
                legacyLookup.TryAdd(ReferenceRelationshipResolver.CreateLookupKey("resolver-fixture", symbol.Signature), symbol);
            var legacyEdges = new ReferenceRelationshipResolver().Resolve(
                [new ReferenceModDecompilation("resolver-fixture", decompilation)], legacyLookup);
            var legacyFields = FieldEdges(legacyEdges);
            Assert.Equal(4, legacyFields.Count);
            Assert.All(legacyFields, edge => Assert.Null(edge.TargetSymbolId));

            var paths = OwnedIndexPaths.ForReferenceMod(root, legacyId);
            Directory.CreateDirectory(paths.StagingRoot);
            var source = await new GeneratedSourceWriter().WriteAsync(
                paths.StagingRoot, "resolver-fixture/ModCheckPlugin.cs", decompilation.SourceText, legacySnapshotId, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O");
            await repository.CreateCodeSnapshotAsync(new CodeSnapshotRecord(
                legacySnapshotId, CodebaseKind.ReferenceMod, CodeChannel.Installed, collection.CollectionId, now), cancellationToken);
            await repository.StartIndexRunAsync(new IndexRunRecord(legacyId, legacySnapshotId, IndexRunStatus.Running, now), cancellationToken);
            var mod = collection.Mods[0];
            await repository.CompleteIndexRunAsync(legacyId,
                new IndexWriteSet(symbols, [source], [], new SymbolFingerprintService().Create(symbols), legacyEdges,
                    ReferenceIndexContext: new ReferenceIndexContextRecord(legacyId, game.IndexId, seed.FromBuildId),
                    ReferenceMods: [new IndexReferenceModRecord(mod.ModId, mod.DisplayName, mod.Version, mod.License,
                        mod.RootPath, inputHash.CollectionContentSha256, symbols.Select(symbol => symbol.SymbolId).ToArray())]),
                now, cancellationToken);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, legacyId + "\n", cancellationToken);

            var workflow = new ReferenceModIndexWorkflow(root, repository, new ReferenceModFileSelector(),
                new ReferenceModInputHasher(), new ReferenceModIndexSource(new IlSpyManagedDecompiler()), new ReferenceGameSymbolLoader(repository));
            var rebuilt = await workflow.RunAsync(seed.FromBuildId, collection, false, cancellationToken);

            Assert.False(rebuilt.Reused);
            Assert.NotEqual(legacyId, rebuilt.IndexId);
            var field = Assert.Single(game.Symbols, symbol => symbol.Kind == "Field" && symbol.QualifiedName == "Demo.Api::System.Int32 Count");
            var rebuiltFields = FieldEdges(await repository.GetCompletedRelationshipsAsync(rebuilt.IndexId, cancellationToken));
            Assert.Equal(4, rebuiltFields.Count);
            Assert.All(rebuiltFields, edge => Assert.Equal(field.SymbolId, edge.TargetSymbolId));
            Assert.Contains(rebuiltFields, edge => edge.Kind == "ReadsField" && edge.TargetText!.StartsWith("Demo.Api::", StringComparison.Ordinal));
            Assert.Contains(rebuiltFields, edge => edge.Kind == "WritesField" && edge.TargetText!.StartsWith("Il2CppDemo.Api::", StringComparison.Ordinal));

            var reused = await workflow.RunAsync(seed.FromBuildId, collection, false, cancellationToken);
            Assert.True(reused.Reused);
            Assert.Equal(rebuilt.IndexId, reused.IndexId);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    private static IReadOnlyList<IndexRelationshipRecord> FieldEdges(IReadOnlyList<IndexRelationshipRecord> edges) =>
        edges.Where(edge => (edge.Kind is "ReadsField" or "WritesField")
            && edge.TargetText is "Demo.Api::System.Int32 Count" or "Il2CppDemo.Api::System.Int32 Count").ToArray();
}
