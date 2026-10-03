using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class PatchedByQueryTests
{
    [Fact]
    public async Task Patched_by_lists_resolved_and_textual_patches()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new ReferenceModQueryService(atlas.Repository, atlas.DataRoot);

        var result = await service.PatchedByAsync(
            "Game.Widget::Run()",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: HarmonyPatchAtlas.CollectionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal("game", result.Resolution.Symbol!.Origin);
        Assert.Equal(9, result.TotalCount);
        Assert.Equal(9, result.Relationships.Count);
        var resolved = result.Relationships.Where(edge => edge.Target.Resolved).ToArray();
        Assert.Equal(8, resolved.Length);
        Assert.All(resolved, edge =>
        {
            Assert.Equal("Patches", edge.Kind);
            Assert.Equal("Prefix", edge.GeneratedDetail);
            Assert.Equal("harmony-fixture", edge.Source.ReferenceModId);
            Assert.Equal("harmony", edge.Source.Collection);
        });
        var labels = resolved.Select(edge => edge.Label).ToArray();
        Assert.Equal(3, labels.Count(label => label == "attribute"));
        Assert.Equal(5, labels.Count(label => label == "DERIVED"));
        var unresolved = Assert.Single(result.Relationships, edge => !edge.Target.Resolved);
        Assert.Equal("attribute", unresolved.Label);
        Assert.Contains("unsupported-method-type", unresolved.Target.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_matches_unresolved_texts_by_type_and_member()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new ReferenceModQueryService(atlas.Repository, atlas.DataRoot);

        var result = await service.PatchedByAsync(
            "Game.Widget::Compute(System.Int32,System.String)",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: HarmonyPatchAtlas.CollectionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal(2, result.Relationships.Count(edge => edge.Target.Resolved));
        var unresolved = result.Relationships.Where(edge => !edge.Target.Resolved).ToArray();
        Assert.Equal(2, unresolved.Length);
        var ambiguous = Assert.Single(unresolved, edge => edge.GeneratedDetail == "Finalizer");
        Assert.Contains("ambiguous-overload", ambiguous.Target.RawText, StringComparison.Ordinal);
        var noOverload = Assert.Single(unresolved, edge => edge.GeneratedDetail == "Prefix");
        Assert.Contains("no-matching-overload", noOverload.Target.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_pages_with_offset_and_limit()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new ReferenceModQueryService(atlas.Repository, atlas.DataRoot);
        var options = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: HarmonyPatchAtlas.CollectionId);

        var first = await service.PatchedByAsync(
            "Game.Widget::Run()",
            options with { Limit = 2 },
            TestContext.Current.CancellationToken);
        var second = await service.PatchedByAsync(
            "Game.Widget::Run()",
            options with { Limit = 2, Offset = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(9, first.TotalCount);
        Assert.Equal(2, first.Relationships.Count);
        Assert.True(first.HasMore);
        Assert.Equal(2, second.Relationships.Count);
        Assert.Empty(first.Relationships.Select(edge => edge.RelationshipId).Intersect(second.Relationships.Select(edge => edge.RelationshipId)));
    }

    [Fact]
    public async Task Federated_patches_fans_out_to_the_reference_collection()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new FederatedIndexQueryService(atlas.Repository, atlas.DataRoot);

        var result = await service.PatchesAsync(
            "Game.Widget::Run()",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.All, ReferenceCollection: HarmonyPatchAtlas.CollectionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(9, result.TotalCount);
    }

    [Fact]
    public async Task Game_patches_resolve_empty_with_a_reference_scope_notice()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new IndexQueryService(atlas.Repository, atlas.DataRoot);

        var result = await service.PatchesAsync(
            "Game.Widget::Run()",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.Game),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Empty(result.Relationships);
        Assert.Equal(0, result.TotalCount);
        Assert.Contains("--scope reference", result.CompletenessNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_returns_marker_edges_for_the_patch_method_selector()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new ReferenceModQueryService(atlas.Repository, atlas.DataRoot);

        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(
            atlas.Seed.ReferenceIndexId, TestContext.Current.CancellationToken);
        var prefix = symbols.Single(symbol => symbol.Signature.Contains("TargetMethodPatch::Prefix(", StringComparison.Ordinal));

        var result = await service.PatchedByAsync(
            prefix.Signature,
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: HarmonyPatchAtlas.CollectionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        var row = Assert.Single(result.Relationships);
        Assert.False(row.Target.Resolved);
        Assert.Contains("runtime-computed-target", row.Target.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_without_a_collection_reports_no_completed_index()
    {
        await using var atlas = await QueryAtlas.CreateAsync();
        var service = new FederatedIndexQueryService(atlas.Repository, atlas.DataRoot);

        var result = await service.PatchesAsync(
            "Game.Widget::Run()",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.All, ReferenceCollection: "missing-collection"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, result.Resolution.Status);
        Assert.Empty(result.Relationships);
    }

    private sealed class QueryAtlas : IAsyncDisposable
    {
        private readonly string _root;

        private QueryAtlas(string root, SqliteAtlasRepository repository)
        {
            _root = root;
            Repository = repository;
            DataRoot = Path.Combine(root, "atlas");
        }

        public SqliteAtlasRepository Repository { get; }

        public string DataRoot { get; }

        public HarmonyPatchSeed Seed { get; private set; } = null!;

        public static async Task<QueryAtlas> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "s1atlas-harmony-query-" + Guid.NewGuid().ToString("N"));
            var dataRoot = Path.Combine(root, "atlas");
            Directory.CreateDirectory(dataRoot);
            var repository = new SqliteAtlasRepository(Path.Combine(dataRoot, "atlas.db"), Path.Combine(dataRoot, "backups"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var atlas = new QueryAtlas(root, repository);
            atlas.Seed = await HarmonyPatchAtlas.SeedAsync(repository, dataRoot, TestContext.Current.CancellationToken);
            return atlas;
        }

        public async ValueTask DisposeAsync()
        {
            await TestDirectory.DeleteTreeAsync(_root);
        }
    }
}
