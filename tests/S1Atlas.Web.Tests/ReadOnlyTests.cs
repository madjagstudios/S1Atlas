using System.Security.Cryptography;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// Serving must never change the atlas: every file under the data root keeps
// identical bytes no matter which pages or API endpoints are read.
public sealed class ReadOnlyTests
{
    [Fact]
    public async Task ServingLeavesEveryAtlasFileUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var before = Snapshot(fixture.Atlas.DataRoot);

        string[] paths =
        [
            "/",
            "/search?q=Widget",
            "/search?q=PagedType&page=1",
            "/search?q=Catalog&codebase=s1api",
            $"/symbol/{SyntheticAtlas.WidgetTypeId}",
            $"/symbol/{SyntheticAtlas.RunMethodId}",
            $"/symbol/{SyntheticAtlas.LookupMethodId}",
            "/api/status",
            "/api/search?q=Widget",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callees",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/references",
        ];
        foreach (var path in paths)
        {
            await fixture.GetStringAsync(path, cancellationToken);
        }

        var after = Snapshot(fixture.Atlas.DataRoot);
        Assert.Equal(before.Keys.Order().ToArray(), after.Keys.Order().ToArray());
        foreach (var relative in before.Keys)
        {
            Assert.True(
                before[relative].SequenceEqual(after[relative]),
                $"Serving changed '{relative}'.");
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string dataRoot)
    {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(dataRoot, file)] = SHA256.HashData(File.ReadAllBytes(file));
        }

        return snapshot;
    }
}
