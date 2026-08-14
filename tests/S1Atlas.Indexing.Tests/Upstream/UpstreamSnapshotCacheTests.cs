using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using Xunit;

namespace S1Atlas.Indexing.Tests.Upstream;

public sealed class UpstreamSnapshotCacheTests
{
    [Fact]
    public async Task Lists_only_commits_with_a_complete_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-upstream-cache-" + Guid.NewGuid().ToString("N"));
        var commit = new string('a', 40);
        try
        {
            var cache = new UpstreamSnapshotCache(root);
            await cache.SaveAsync(
                new UpstreamSnapshot(
                    new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo"),
                    commit,
                    [new UpstreamFile("src/Api.cs", [1, 2, 3], "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81")]),
                TestContext.Current.CancellationToken);

            Assert.Contains(commit, cache.GetCachedCommits(CodebaseKind.S1Api));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
