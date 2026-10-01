using S1Atlas.Application.Composition;
using S1Atlas.Core.Indexing;
using S1Atlas.TestSupport.Seeding;
using S1Atlas.Web.Queries;
using Xunit;

namespace S1Atlas.Web.Tests;

[Collection("TwoBuildServe")]
public sealed class DiffCacheTests
{
    private readonly SharedTwoBuildServeFixture _shared;

    public DiffCacheTests(SharedTwoBuildServeFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task DiffAsync_ReturnsSameResultWithoutRecomputing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var queries = new ServeQueries(ReadOnlyAtlasComposition.BuildReadOnlyServices(_shared.Serve.Atlas.DataRoot));

        var first = await queries.DiffAsync(
            SyntheticAtlas.GameIndexAValue, SyntheticAtlas.GameIndexBValue, null, cancellationToken);
        var second = await queries.DiffAsync(
            SyntheticAtlas.GameIndexAValue, SyntheticAtlas.GameIndexBValue, null, cancellationToken);

        Assert.Same(first, second);
        Assert.NotEmpty(first.Changes);
    }

    [Fact]
    public async Task DiffAsync_CachesPerKindFilter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var queries = new ServeQueries(ReadOnlyAtlasComposition.BuildReadOnlyServices(_shared.Serve.Atlas.DataRoot));

        var unfiltered = await queries.DiffAsync(
            SyntheticAtlas.GameIndexAValue, SyntheticAtlas.GameIndexBValue, null, cancellationToken);
        var methods = await queries.DiffAsync(
            SyntheticAtlas.GameIndexAValue, SyntheticAtlas.GameIndexBValue, "Method", cancellationToken);

        Assert.NotSame(unfiltered, methods);
        Assert.All(methods.Changes, change => Assert.Equal("Method", change.Kind));
    }

    [Fact]
    public async Task Cache_EvictsOldestBeyondBound()
    {
        var cache = new DiffResultCache();
        var computations = 0;
        Task<BuildDiffResult> Compute(string tag)
        {
            computations++;
            return Task.FromResult(new BuildDiffResult(
                "a", "b", "ScheduleI", "Installed", 0, 0,
                new Dictionary<DiffClassification, int>(),
                [new SymbolDiff(tag, tag, "Type", DiffClassification.Added, null, null)]));
        }

        for (var number = 0; number < 8; number++)
            await cache.GetOrAddAsync(Key(number), () => Compute($"key-{number}"), TestContext.Current.CancellationToken);
        Assert.Equal(8, computations);

        await cache.GetOrAddAsync(Key(0), () => Compute("recomputed"), TestContext.Current.CancellationToken);
        Assert.Equal(8, computations);

        await cache.GetOrAddAsync(Key(8), () => Compute("key-8"), TestContext.Current.CancellationToken);
        Assert.Equal(9, computations);

        await cache.GetOrAddAsync(Key(0), () => Compute("recomputed"), TestContext.Current.CancellationToken);
        Assert.Equal(10, computations);
    }

    [Fact]
    public async Task Cache_DoesNotKeepFailures()
    {
        var cache = new DiffResultCache();
        var attempts = 0;
        Task<BuildDiffResult> Flaky()
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("boom");
            return Task.FromResult(new BuildDiffResult(
                "a", "b", "ScheduleI", "Installed", 0, 0,
                new Dictionary<DiffClassification, int>(), []));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(Key(0), Flaky, TestContext.Current.CancellationToken));
        await cache.GetOrAddAsync(Key(0), Flaky, TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
    }

    private static DiffCacheKey Key(int number) =>
        new($"index-a-{number}", "index-b", "ScheduleI", null);
}
