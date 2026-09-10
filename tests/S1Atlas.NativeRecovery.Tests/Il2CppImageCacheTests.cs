using AssetRipper.Primitives;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class Il2CppImageCacheTests
{
    private static readonly UnityVersion TestUnityVersion = UnityVersion.Parse("2022.3.62f2");

    [Fact]
    public async Task WithImageAsync_FirstCall_InvokesInitializerOnceAndReturnsBodyResult()
    {
        var initCount = 0;
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref initCount);
            return Task.CompletedTask;
        });

        var result = await cache.WithImageAsync(
            "ga-sha", [1, 2, 3],
            "meta-sha", [4, 5, 6],
            TestUnityVersion,
            _ => Task.FromResult(42),
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, initCount);
    }

    [Fact]
    public async Task WithImageAsync_SameKeyTwice_DoesNotReinitialize()
    {
        var initCount = 0;
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref initCount);
            return Task.CompletedTask;
        });

        await cache.WithImageAsync("ga-sha", [1], "meta-sha", [2], TestUnityVersion, _ => Task.FromResult(1), CancellationToken.None);
        await cache.WithImageAsync("ga-sha", [1], "meta-sha", [2], TestUnityVersion, _ => Task.FromResult(2), CancellationToken.None);

        Assert.Equal(1, initCount);
    }

    [Fact]
    public async Task WithImageAsync_DifferentKey_Reinitializes()
    {
        var initCount = 0;
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref initCount);
            return Task.CompletedTask;
        });

        await cache.WithImageAsync("ga-sha-1", [1], "meta-sha-1", [2], TestUnityVersion, _ => Task.FromResult(1), CancellationToken.None);
        await cache.WithImageAsync("ga-sha-2", [1], "meta-sha-1", [2], TestUnityVersion, _ => Task.FromResult(2), CancellationToken.None);

        Assert.Equal(2, initCount);
    }

    [Fact]
    public async Task WithImageAsync_ConcurrentCalls_NeverRunBodiesOverlapping()
    {
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) => Task.CompletedTask);

        var reentrancyCounter = 0;
        var maxObservedConcurrency = 0;
        var maxLock = new object();

        async Task<int> Body(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref reentrancyCounter);
            lock (maxLock)
            {
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, current);
            }

            await Task.Delay(15, ct);

            Interlocked.Decrement(ref reentrancyCounter);
            return current;
        }

        var callTasks = Enumerable.Range(0, 8).Select(_ => cache.WithImageAsync(
            "ga-sha", [1, 2, 3],
            "meta-sha", [4, 5, 6],
            TestUnityVersion,
            Body,
            CancellationToken.None));

        await Task.WhenAll(callTasks);

        Assert.Equal(1, maxObservedConcurrency);
    }

    [Fact]
    public async Task WithImageAsync_InitializerThrows_PropagatesToCallerAndDoesNotUpdateCachedKey()
    {
        var shouldThrow = true;
        var initCount = 0;
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref initCount);
            if (shouldThrow)
            {
                throw new InvalidOperationException("simulated initializer failure");
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.WithImageAsync(
            "ga-sha", [1], "meta-sha", [2], TestUnityVersion, _ => Task.FromResult(0), CancellationToken.None));

        shouldThrow = false;
        var result = await cache.WithImageAsync(
            "ga-sha", [1], "meta-sha", [2], TestUnityVersion, _ => Task.FromResult(42), CancellationToken.None);

        Assert.Equal(2, initCount);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task WithImageAsync_CancelledToken_ThrowsBeforeRunningInitializerOrBody()
    {
        var initCount = 0;
        using var cache = new Il2CppImageCache((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref initCount);
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var bodyRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.WithImageAsync(
            "ga-sha", [1], "meta-sha", [2], TestUnityVersion,
            _ =>
            {
                bodyRan = true;
                return Task.FromResult(0);
            },
            cts.Token));

        Assert.False(bodyRan);
        Assert.Equal(0, initCount);
    }

    [Fact]
    public async Task Dispose_DisposesUnderlyingSemaphore_SoSubsequentCallsThrowObjectDisposedException()
    {
        var cache = new Il2CppImageCache((_, _, _, _, _, _) => Task.CompletedTask);
        cache.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.WithImageAsync(
            "ga-sha", [1], "meta-sha", [2], TestUnityVersion, _ => Task.FromResult(0), CancellationToken.None));
    }
}
