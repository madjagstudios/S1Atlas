using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
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
            [fixture.DealerA.SymbolId, fixture.DealerB.SymbolId],
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
                    dealerB
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
            dealerB);
    }

    private static string HashId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
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
        IndexSymbolRecord DealerB);
}
