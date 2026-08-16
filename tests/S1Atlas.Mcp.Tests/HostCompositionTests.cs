using S1Atlas.Mcp;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class HostCompositionTests
{
    [Fact]
    public async Task Composition_OverSeededAtlas_ResolvesAuthorityReadOnly()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);

        var authority = await services.AuthorityResolver.ResolveAsync(null, CancellationToken.None);

        Assert.Equal("Resolved", authority.Status.ToString());
        Assert.False(
            File.Exists(Path.Combine(atlas.DataRoot, "atlas.db.tmp")),
            "composition must not create scratch files");
    }

    [Fact]
    public void RegisteredTools_ContainNoMutationVerbs()
    {
        var toolNames = McpToolCatalog.DiscoverToolNames();
        var forbiddenVerbs = new[]
        {
            "extract",
            "promote",
            "cleanup",
            "install",
            "scan",
            "index",
            "sync",
            "delete",
            "write",
            "set"
        };

        Assert.All(
            toolNames,
            name => Assert.DoesNotContain(
                forbiddenVerbs,
                verb => name.Contains(verb, StringComparison.OrdinalIgnoreCase)));
    }
}
