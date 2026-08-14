using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Upstream;

public sealed class UpstreamSyncService
{
    private readonly IUpstreamClient _client;
    private readonly UpstreamSnapshotCache _cache;

    public UpstreamSyncService(IUpstreamClient client, UpstreamSnapshotCache cache)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<UpstreamSyncResult> SyncAsync(UpstreamRepositoryConfiguration repository, string commitSha, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _client.FetchAsync(repository, commitSha, cancellationToken);
            await _cache.SaveAsync(snapshot, cancellationToken);
            return new UpstreamSyncResult(repository.Codebase, commitSha, false, false, snapshot.Files.Count, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (_cache.Exists(repository.Codebase, commitSha))
                return new UpstreamSyncResult(repository.Codebase, commitSha, true, true, 0, exception.Message);
            throw;
        }
    }
}
