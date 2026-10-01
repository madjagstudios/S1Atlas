using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Queries;

public sealed record DiffCacheKey(
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string? KindFilter);

// Small bounded FIFO of diff results. The atlas is immutable while serve
// runs, so entries need no invalidation beyond the bound; failures are
// never kept.
public sealed class DiffResultCache
{
    internal const int MaxEntries = 8;

    private readonly object _sync = new();
    private readonly Dictionary<DiffCacheKey, Task<BuildDiffResult>> _entries = new();
    private readonly Queue<DiffCacheKey> _insertionOrder = new();

    public async Task<BuildDiffResult> GetOrAddAsync(
        DiffCacheKey key,
        Func<Task<BuildDiffResult>> compute,
        CancellationToken cancellationToken)
    {
        Task<BuildDiffResult> pending;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                pending = cached;
            }
            else
            {
                pending = compute();
                _entries[key] = pending;
                _insertionOrder.Enqueue(key);
                while (_entries.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
                {
                    _entries.Remove(oldest);
                }
            }
        }

        try
        {
            return await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    _entries.Remove(key);
                }
            }

            throw;
        }
    }
}
