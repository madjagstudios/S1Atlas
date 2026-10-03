using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Queries;

public sealed record DiffCacheKey(
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string? KindFilter);

// Small bounded FIFO of diff results. The atlas is immutable while serve
// runs, so entries need no invalidation beyond the bound; failures are
// never kept. The shared computation is detached from any single caller:
// each waiter cancels only its own wait, the compute delegate runs outside
// the lock exactly once, and a faulted entry evicts itself through a
// continuation so a failure no waiter observes cannot poison later calls.
public sealed class DiffResultCache
{
    internal const int MaxEntries = 8;

    private readonly object _sync = new();
    private readonly Dictionary<DiffCacheKey, CacheEntry> _entries = new();
    private readonly Queue<(DiffCacheKey Key, long Sequence)> _insertionOrder = new();
    private long _sequence;

    public async Task<BuildDiffResult> GetOrAddAsync(
        DiffCacheKey key,
        Func<Task<BuildDiffResult>> compute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var (entry, created) = GetOrCreateEntry(key, compute);
        Task<BuildDiffResult> pending;
        try
        {
            pending = entry.Computation.Value;
        }
        catch
        {
            Evict(key, entry);
            throw;
        }

        if (created)
        {
            _ = pending.ContinueWith(
                task =>
                {
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        Evict(key, entry);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            return await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only this waiter's wait was cancelled; the shared computation
            // keeps running for the other waiters.
            throw;
        }
    }

    private (CacheEntry Entry, bool Created) GetOrCreateEntry(
        DiffCacheKey key,
        Func<Task<BuildDiffResult>> compute)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                return (cached, false);
            }

            var entry = new CacheEntry(
                new Lazy<Task<BuildDiffResult>>(compute, LazyThreadSafetyMode.ExecutionAndPublication),
                ++_sequence);
            _entries[key] = entry;
            _insertionOrder.Enqueue((key, entry.Sequence));
            while (_entries.Count > MaxEntries && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                if (_entries.TryGetValue(oldest.Key, out var current) &&
                    current.Sequence == oldest.Sequence)
                {
                    _entries.Remove(oldest.Key);
                }
            }

            return (entry, true);
        }
    }

    private void Evict(DiffCacheKey key, CacheEntry entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class CacheEntry(Lazy<Task<BuildDiffResult>> computation, long sequence)
    {
        public Lazy<Task<BuildDiffResult>> Computation { get; } = computation;

        public long Sequence { get; } = sequence;
    }
}
