using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class DocsGenerateCommandTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-docs-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Generate_missing_database_reports_scan_first_and_creates_no_site()
    {
        var dataRoot = Path.Combine(_root, "atlas");
        var output = Path.Combine(_root, "site");
        Directory.CreateDirectory(dataRoot);

        var result = CliRunner.Run(dataRoot, "docs", "generate", "--output", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("scan or migration first", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Generate_writes_site_from_repository_owned_fixture()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();
        var output = Path.Combine(_root, "site");

        var result = CliRunner.Run(atlas.DataRoot, "docs", "generate", "--output", output);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "search.html")));
        Assert.True(File.Exists(Path.Combine(output, "assets", "search-index.js")));
        Assert.Contains("latest completed index", await File.ReadAllTextAsync(Path.Combine(output, "code", "s1api", "installed", "index.html"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
