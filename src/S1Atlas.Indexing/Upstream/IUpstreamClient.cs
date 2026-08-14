using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Upstream;

public interface IUpstreamClient
{
    Task<UpstreamSnapshot> FetchAsync(
        UpstreamRepositoryConfiguration repository,
        string commitSha,
        CancellationToken cancellationToken);
}
