using System.Security.Cryptography;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using Xunit;

namespace S1Atlas.Indexing.Tests.Upstream;

public sealed class UpstreamSyncServiceTests
{
    [Fact]
    public async Task Sync_uses_fake_client_and_validates_cached_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-upstream-" + Guid.NewGuid().ToString("N"));
        var commit = new string('a', 40);
        var repository = new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo");
        var bytes = "class Demo {}"u8.ToArray();
        try
        {
            var client = new FakeClient(new UpstreamSnapshot(repository, commit, [new UpstreamFile("Demo.cs", bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())]));
            var service = new UpstreamSyncService(client, new UpstreamSnapshotCache(root));
            var result = await service.SyncAsync(repository, commit, TestContext.Current.CancellationToken);

            Assert.False(result.ReusedCache);
            Assert.Equal(1, result.FileCount);
            Assert.True(new UpstreamSnapshotCache(root).Exists(CodebaseKind.S1Api, commit));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeClient(UpstreamSnapshot snapshot) : IUpstreamClient
    {
        public Task<UpstreamSnapshot> FetchAsync(UpstreamRepositoryConfiguration repository, string commitSha, CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
