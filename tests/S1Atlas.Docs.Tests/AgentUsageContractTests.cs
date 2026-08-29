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
        var sharedGuidance = """
            ### Host parity and efficient use

            Codex and Claude use the same read-only S1Atlas MCP server. When the server is
            registered and available, use MCP for discovery and queries; otherwise use the
            CLI's JSON output. Verify availability before relying on MCP, and never treat a
            missing server as an empty index.

            For prior-art work, list completed collections once, choose one explicit
            collection, and retain its recorded Schedule I base index. Resolve the exact
            symbol before reading source. Read the focused span first, then request only
            the bounded callers, callees, call sites, field references, or related types
            needed for the question. Do not repeat equivalent MCP and CLI queries or issue
            unscoped searches across every collection.

            Carry the returned build, extraction, index, collection, mod, relative-path,
            and content-hash provenance into the decision. Static relationship evidence,
            callability, and source runtime-verification hints remain distinct from live
            runtime behavior.
            """.ReplaceLineEndings("\n");

        Assert.Contains("dotnet run --project src/S1Atlas.Mcp -- mcp serve", combined, StringComparison.Ordinal);
        Assert.Contains("list_reference_collections", combined, StringComparison.Ordinal);
        Assert.Contains("CLI JSON", combined, StringComparison.Ordinal);
        Assert.Contains("unavailable server", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content hash", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not download mods", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sharedGuidance, skill.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(sharedGuidance, usage.ReplaceLineEndings("\n"), StringComparison.Ordinal);
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
