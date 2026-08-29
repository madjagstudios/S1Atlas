using Xunit;

namespace S1Atlas.Docs.Tests;

public sealed class AgentUsageContractTests
{
    [Fact]
    public void SharedGuidanceDefinesParitySelectionAndTrustRules()
    {
        var root = FindRepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(root, "skills", "s1atlas", "SKILL.md"));
        var usage = File.ReadAllText(Path.Combine(root, "docs", "USAGE.md"));
        var combined = skill + Environment.NewLine + usage;

        Assert.Contains("dotnet run --project src/S1Atlas.Mcp -- mcp serve", combined, StringComparison.Ordinal);
        Assert.Contains("list_reference_collections", combined, StringComparison.Ordinal);
        Assert.Contains("CLI JSON", combined, StringComparison.Ordinal);
        Assert.Contains("unavailable server", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content hash", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not download mods", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
