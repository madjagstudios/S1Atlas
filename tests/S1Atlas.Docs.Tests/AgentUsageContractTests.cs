using Xunit;

namespace S1Atlas.Docs.Tests;

public sealed class AgentUsageContractTests
{
    [Fact]
    public void CanonicalSkillDefinesTheFullAgentContract()
    {
        var root = FindRepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(root, "skills", "s1atlas", "SKILL.md")).ReplaceLineEndings("\n");
        var normalizedSkill = NormalizeWhitespace(skill);

        AssertPortableContract(skill);
        Assert.Contains("### Host parity and efficient use", skill, StringComparison.Ordinal);
        Assert.Contains("## Authority boundary and provenance contract", skill, StringComparison.Ordinal);
        Assert.Contains("list completed collections once, choose one explicit collection", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Resolve the exact symbol before reading source", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Read the focused span first, then request only the bounded callers, callees, call sites, field references, or related types needed for the question", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Do not repeat equivalent MCP and CLI queries", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Static relationship evidence, callability, and source runtime-verification hints remain distinct from live runtime behavior", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Carry the returned build, extraction, index, collection, mod, relative-path, and content-hash provenance into the decision", normalizedSkill, StringComparison.Ordinal);
    }

    [Fact]
    public void UsagePointsToTheSkillAndRetainsOperationalSafetyFacts()
    {
        var root = FindRepositoryRoot();
        var usage = File.ReadAllText(Path.Combine(root, "docs", "USAGE.md")).ReplaceLineEndings("\n");
        var normalizedUsage = NormalizeWhitespace(usage);

        Assert.Contains("[`skills/s1atlas/SKILL.md`](../skills/s1atlas/SKILL.md)", usage, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/S1Atlas.Mcp -- mcp serve", usage, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project <local-S1Atlas-root>/src/S1Atlas.Mcp/S1Atlas.Mcp.csproj -- mcp serve", usage, StringComparison.Ordinal);
        Assert.Contains("Each host registration should enable the read-only server and use bounded startup/tool timeouts, with those settings kept in user-level config.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("skill's CLI", usage, StringComparison.Ordinal);
        Assert.Contains("commands remain the fallback", usage, StringComparison.Ordinal);
        Assert.Contains("read-only server entry point", usage, StringComparison.Ordinal);
        Assert.Contains("Host configuration and reference manifests stay outside the repository", usage, StringComparison.Ordinal);
        Assert.Contains("S1Atlas does not download mods.", normalizedUsage, StringComparison.Ordinal);
        Assert.DoesNotContain("### Host parity and efficient use", usage, StringComparison.Ordinal);
        Assert.DoesNotContain("For prior-art work, list completed collections once", usage, StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string document)
    {
        return string.Join(" ", document.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AssertPortableContract(string document)
    {
        Assert.Contains("list_reference_collections", document, StringComparison.Ordinal);
        Assert.Contains("CLI's JSON output", document, StringComparison.Ordinal);
        Assert.Contains("missing server as an empty index", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content hash", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not download mods", document, StringComparison.OrdinalIgnoreCase);
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
