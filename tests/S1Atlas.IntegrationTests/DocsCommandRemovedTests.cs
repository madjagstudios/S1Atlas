using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class DocsCommandRemovedTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-docs-removed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Docs_generate_fails_with_normal_unknown_command_error()
    {
        var dataRoot = Path.Combine(_root, "atlas");
        Directory.CreateDirectory(dataRoot);

        var removed = CliRunner.Run(dataRoot, "docs", "generate");
        var bogus = CliRunner.Run(dataRoot, "frobnicate-wibble");

        AssertUnknownCommandError(removed, bogus, "docs");
    }

    [Fact]
    public void Docs_bare_fails_with_normal_unknown_command_error()
    {
        var dataRoot = Path.Combine(_root, "atlas");
        Directory.CreateDirectory(dataRoot);

        var removed = CliRunner.Run(dataRoot, "docs");
        var bogus = CliRunner.Run(dataRoot, "frobnicate-wibble");

        AssertUnknownCommandError(removed, bogus, "docs");
    }

    private static void AssertUnknownCommandError(
        (int ExitCode, string StandardOutput, string StandardError) removed,
        (int ExitCode, string StandardOutput, string StandardError) bogus,
        string token)
    {
        Assert.NotEqual(0, bogus.ExitCode);
        Assert.Contains("frobnicate-wibble", bogus.StandardError, StringComparison.Ordinal);
        Assert.Equal(bogus.ExitCode, removed.ExitCode);
        Assert.Contains(token, removed.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("deprecat", removed.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scan or migration first", removed.StandardError, StringComparison.OrdinalIgnoreCase);

        var bogusFirstLine = bogus.StandardError.Split('\n')[0].Trim().Replace("frobnicate-wibble", token, StringComparison.Ordinal);
        Assert.Contains(bogusFirstLine, removed.StandardError, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
