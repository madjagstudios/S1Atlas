using S1Atlas.Cli;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class IndexCliTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-index-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Index_json_is_registered_and_fails_closed_without_authority()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["index", "--json"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("NoEnvironmentSnapshot", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
