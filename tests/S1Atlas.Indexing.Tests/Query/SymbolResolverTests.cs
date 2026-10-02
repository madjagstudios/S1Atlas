using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class SymbolResolverTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-symbol-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SymbolResolverTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Exact_symbol_id_is_the_strongest_selector()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactId.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.ExactId.SymbolId, result.Symbol?.SymbolId);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Resolved_symbol_carries_twelve_character_short_id()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactId.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.NotNull(result.Symbol);
        Assert.Equal(fixture.ExactId.SymbolId[..12], result.Symbol.ShortId);
    }

    [Fact]
    public async Task Exact_canonical_key_or_signature_resolves_before_textual_matching()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var byKey = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Canonical.CanonicalKey,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);
        var bySignature = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.Signature,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, byKey.Status);
        Assert.Equal(fixture.Canonical.SymbolId, byKey.Symbol?.SymbolId);
        Assert.Equal(SymbolResolutionStatus.Resolved, bySignature.Status);
        Assert.Equal(fixture.Signature.SymbolId, bySignature.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Unique_exact_qualified_name_resolves_before_lower_ranked_matches()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactName.QualifiedName,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.ExactName.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Unique_best_ranked_textual_match_resolves()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "inventory",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.UniqueText.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Equal_best_rank_is_ambiguous_and_never_first_row_wins()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "dealer",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Symbol);
        Assert.Equal(
            new[] { fixture.DealerA.SymbolId, fixture.DealerB.SymbolId }.Order(StringComparer.Ordinal),
            result.Candidates.Select(candidate => candidate.SymbolId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Kinded_id_of_matching_kind_resolves()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Canonical.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.Canonical.SymbolId, result.Symbol?.SymbolId);
        Assert.IsType<SymbolResolutionResult>(result);
    }

    [Fact]
    public async Task Kinded_search_resolves_type_despite_fifty_other_kind_matches()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Crowd",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.CrowdType.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Kinded_id_of_wrong_kind_returns_not_found_with_mismatch()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Candidates);
        var kinded = Assert.IsType<KindedSymbolResolutionResult>(result);
        Assert.Equal(fixture.Signature.SymbolId, kinded.KindMismatch.SymbolId);
        Assert.Equal("Method", kinded.KindMismatch.Kind);
    }

    [Fact]
    public async Task Kinded_canonical_key_of_wrong_kind_returns_not_found_with_mismatch()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.CanonicalKey,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        var kinded = Assert.IsType<KindedSymbolResolutionResult>(result);
        Assert.Equal(fixture.Signature.SymbolId, kinded.KindMismatch.SymbolId);
    }

    [Fact]
    public async Task Kinded_absent_canonical_key_does_not_fall_through_to_fuzzy_matching()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "S1Api:Release:Type:ExactWidget",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.IsType<SymbolResolutionResult>(result);
    }

    [Fact]
    public async Task Multi_kind_set_merges_searches_and_excludes_other_kinds()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Factory",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Method, SymbolKind.Constructor });

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(
            new[] { fixture.FactoryMethod.SymbolId, fixture.FactoryCtor.SymbolId }.Order(StringComparer.Ordinal),
            result.Candidates.Select(candidate => candidate.SymbolId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task No_matching_symbol_returns_not_found()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "definitely-not-present",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Candidates);
    }

    private async Task<ResolverFixture> SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver";
        const string indexId = "index-resolver";
        const CodebaseKind codebase = CodebaseKind.S1Api;
        const CodeChannel channel = CodeChannel.Release;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            codebase,
            channel,
            "resolver-source",
            "2026-08-14T03:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(string key, string kind, string name, string signature) =>
            new(
                HashId(snapshotId + "\n" + key),
                snapshotId,
                key,
                kind,
                name,
                signature,
                false);

        var exactId = Symbol(
            "S1Api:Release:Type:Demo.ById",
            "Type",
            "Demo.ById",
            "Demo.ById");
        var canonical = Symbol(
            "S1Api:Release:Type:Demo.CanonicalTarget",
            "Type",
            "Demo.CanonicalTarget",
            "Demo.CanonicalTarget");
        var signature = Symbol(
            "S1Api:Release:Method:Demo.Signatures::Execute(System.Int32)",
            "Method",
            "Demo.Signatures.Execute",
            "System.Void Demo.Signatures::Execute(System.Int32)");
        var exactName = Symbol(
            "S1Api:Release:Type:Demo.ExactWidget",
            "Type",
            "Demo.ExactWidget",
            "Demo.ExactWidget");
        var lowerExactName = Symbol(
            "S1Api:Release:Type:Demo.ExactWidgetHelper",
            "Type",
            "Demo.ExactWidgetHelper",
            "Demo.ExactWidgetHelper");
        var uniqueText = Symbol(
            "S1Api:Release:Type:InventoryService",
            "Type",
            "InventoryService",
            "InventoryService");
        var lowerText = Symbol(
            "S1Api:Release:Type:Demo.SuperInventoryArchive",
            "Type",
            "Demo.SuperInventoryArchive",
            "Demo.SuperInventoryArchive");
        var dealerA = Symbol(
            "S1Api:Release:Type:Alpha.DealerService",
            "Type",
            "Alpha.DealerService",
            "Alpha.DealerService");
        var dealerB = Symbol(
            "S1Api:Release:Type:Beta.DealerService",
            "Type",
            "Beta.DealerService",
            "Beta.DealerService");
        var crowdMethods = Enumerable.Range(0, 50)
            .Select(i => Symbol(
                $"S1Api:Release:Method:Crowd.A{i:00}::Run()",
                "Method",
                $"Crowd.A{i:00}.Run",
                $"System.Void Crowd.A{i:00}::Run()"))
            .ToArray();
        var crowdType = Symbol(
            "S1Api:Release:Type:Crowd.Target",
            "Type",
            "Crowd.Target",
            "Crowd.Target");
        var factoryType = Symbol(
            "S1Api:Release:Type:Demo.Factory",
            "Type",
            "Demo.Factory",
            "Demo.Factory");
        var factoryMethod = Symbol(
            "S1Api:Release:Method:Demo.Factory::Build()",
            "Method",
            "Demo.Factory.Build",
            "System.Void Demo.Factory::Build()");
        var factoryCtor = Symbol(
            "S1Api:Release:Constructor:Demo.Factory::.ctor()",
            "Constructor",
            "Demo.Factory.New",
            "System.Void Demo.Factory::.ctor()");

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [
                    exactId,
                    canonical,
                    signature,
                    exactName,
                    lowerExactName,
                    uniqueText,
                    lowerText,
                    dealerA,
                    dealerB,
                    ..crowdMethods,
                    crowdType,
                    factoryType,
                    factoryMethod,
                    factoryCtor
                ],
                [],
                [],
                [],
                []),
            "2026-08-14T03:01:00Z",
            cancellationToken);

        return new ResolverFixture(
            indexId,
            codebase,
            channel,
            exactId,
            canonical,
            signature,
            exactName,
            uniqueText,
            dealerA,
            dealerB,
            crowdType,
            factoryMethod,
            factoryCtor);
    }

    private static string HashId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed record ResolverFixture(
        string IndexId,
        CodebaseKind Codebase,
        CodeChannel Channel,
        IndexSymbolRecord ExactId,
        IndexSymbolRecord Canonical,
        IndexSymbolRecord Signature,
        IndexSymbolRecord ExactName,
        IndexSymbolRecord UniqueText,
        IndexSymbolRecord DealerA,
        IndexSymbolRecord DealerB,
        IndexSymbolRecord CrowdType,
        IndexSymbolRecord FactoryMethod,
        IndexSymbolRecord FactoryCtor);
}
