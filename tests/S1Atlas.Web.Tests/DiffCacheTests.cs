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

    [Fact]
    public async Task Cache_CancelledWaitDoesNotDisturbTheSharedComputation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new DiffResultCache();
        var gate = new TaskCompletionSource<BuildDiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computations = 0;
        Task<BuildDiffResult> Compute()
        {
            computations++;
            started.TrySetResult();
            return gate.Task;
        }

        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = cache.GetOrAddAsync(Key(0), Compute, waiterCts.Token);
        await started.Task.WaitAsync(cancellationToken);

        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var expected = Result("shared");
        gate.TrySetResult(expected);

        var second = await cache.GetOrAddAsync(Key(0), Compute, cancellationToken);
        Assert.Same(expected, second);
        Assert.Equal(1, computations);
    }

    [Fact]
    public async Task Cache_EvictsACancelledComputationNoWaiterObserves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new DiffResultCache();
        // Default TCS: continuations run inline, so cancelling the gate
        // evicts the entry synchronously with no sleep.
        var gate = new TaskCompletionSource<BuildDiffResult>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computations = 0;
        Task<BuildDiffResult> Compute()
        {
            computations++;
            started.TrySetResult();
            return computations == 1 ? gate.Task : Task.FromResult(Result("recomputed"));
        }

        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = cache.GetOrAddAsync(Key(0), Compute, waiterCts.Token);
        await started.Task.WaitAsync(cancellationToken);

        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.TrySetCanceled(cancellationToken);

        var second = await cache.GetOrAddAsync(Key(0), Compute, cancellationToken);
        Assert.Equal("recomputed", SingleTag(second));
        Assert.Equal(2, computations);
    }

    [Fact]
    public async Task Cache_ComputesExactlyOnceUnderConcurrency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new DiffResultCache();
        var gate = new TaskCompletionSource<BuildDiffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computations = 0;
        Task<BuildDiffResult> Compute()
        {
            Interlocked.Increment(ref computations);
            started.TrySetResult();
            return gate.Task;
        }

        var racers = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return await cache.GetOrAddAsync(Key(0), Compute, cancellationToken);
        }, cancellationToken)).ToArray();
        start.SetResult();
        await started.Task.WaitAsync(cancellationToken);
        gate.TrySetResult(Result("one"));

        var results = await Task.WhenAll(racers).WaitAsync(cancellationToken);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, computations);
    }

    [Fact]
    public async Task Cache_StaleQueueEntryDoesNotEvictTheReaddedKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new DiffResultCache();
        var computations = new Dictionary<int, int>();
        Task<BuildDiffResult> Compute(int number, bool fail = false)
        {
            computations[number] = computations.GetValueOrDefault(number) + 1;
            return fail
                ? Task.FromException<BuildDiffResult>(new InvalidOperationException("boom"))
                : Task.FromResult(Result($"key-{number}"));
        }

        // Key 0 fails: evicted from the map while its queue slot goes stale.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(Key(0), () => Compute(0, fail: true), cancellationToken));
        for (var number = 1; number < 8; number++)
            await cache.GetOrAddAsync(Key(number), () => Compute(number), cancellationToken);

        // Re-added key 0 is now the newest live entry.
        await cache.GetOrAddAsync(Key(0), () => Compute(0), cancellationToken);
        // Overflow must evict key 1 (oldest live), not key 0.
        await cache.GetOrAddAsync(Key(8), () => Compute(8), cancellationToken);

        await cache.GetOrAddAsync(Key(0), () => Compute(0), cancellationToken);
        Assert.Equal(2, computations[0]);
        await cache.GetOrAddAsync(Key(1), () => Compute(1), cancellationToken);
        Assert.Equal(2, computations[1]);
    }

    [Fact]
    public async Task Cache_DoesNotRunComputeUnderTheLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new DiffResultCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = Result("blocked");
        Task<BuildDiffResult> BlockingCompute()
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Task.FromResult(expected);
        }

        var blockedCall = Task.Run(
            () => cache.GetOrAddAsync(Key(0), BlockingCompute, cancellationToken), cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        // This call must complete while the other compute is still blocked.
        var other = await cache.GetOrAddAsync(
            Key(1), () => Task.FromResult(Result("other")), cancellationToken);
        Assert.Equal("other", SingleTag(other));

        release.SetResult();
        Assert.Same(expected, await blockedCall);
    }

    private static DiffCacheKey Key(int number) =>
        new($"index-a-{number}", "index-b", "ScheduleI", null);

    private static BuildDiffResult Result(string tag) =>
        new(
            "a",
            "b",
            "ScheduleI",
            "Installed",
            0,
            0,
            new Dictionary<DiffClassification, int>(),
            [new SymbolDiff(tag, tag, "Type", DiffClassification.Added, null, null)]);

    private static string SingleTag(BuildDiffResult result) =>
        Assert.Single(result.Changes).CanonicalKey;
}
