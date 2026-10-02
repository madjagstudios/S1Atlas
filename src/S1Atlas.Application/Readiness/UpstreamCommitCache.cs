using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;

namespace S1Atlas.Application.Readiness;

public sealed class UpstreamCommitCache : IUpstreamCommitCache
{
    private readonly UpstreamSnapshotCache _cache;

    public UpstreamCommitCache(string dataRoot)
    {
        _cache = new UpstreamSnapshotCache(dataRoot);
    }

    public IReadOnlyList<string> GetCachedCommits(CodebaseKind codebase) =>
        _cache.GetCachedCommits(codebase);
}
