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

    [Fact]
    public void McpProject_DoesNotReferenceCliOrExtractionProjects()
    {
        var projectFile = File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "S1Atlas.Mcp.csproj"));

        Assert.DoesNotContain("S1Atlas.Cli", projectFile, StringComparison.Ordinal);
        Assert.DoesNotContain("S1Atlas.Extraction", projectFile, StringComparison.Ordinal);
    }

    [Fact]
    public void McpComposition_UsesSharedApplicationFactoryWithoutReflection()
    {
        var source = File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "McpServerComposition.cs"));
        source += File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "Tools", "CodeSymbolTools.cs"));
        source += File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "Tools", "ReferenceCollectionTools.cs"));

        Assert.Contains("ReadOnlyAtlasComposition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Activator.CreateInstance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidatedExtractionIntegrityVerifier", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
    }

    private static string GetRepoPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }
}
