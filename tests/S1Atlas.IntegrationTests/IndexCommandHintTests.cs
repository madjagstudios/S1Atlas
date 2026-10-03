using System.Text.Json;
using S1Atlas.Application.Readiness;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class IndexCommandHintTests : IAsyncDisposable
{
    private readonly HintCliHarness _harness = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    public static TheoryData<string[]> NoSnapshotVariants => new()
    {
        { ["index"] },
        { ["index", "--scene"] },
        { ["index", "--codebase", "s1api", "--channel", "installed"] }
    };

    [Theory]
    [MemberData(nameof(NoSnapshotVariants))]
    public void Index_WithoutSnapshot_NamesScan(string[] args)
    {
        var (exitCode, _, stderr) = _harness.Invoke(args);

        Assert.Equal(1, exitCode);
        Assert.Contains("Code:    NoEnvironmentSnapshot", stderr, StringComparison.Ordinal);
        Assert.Contains($"Next:    {ReadinessFixCommands.Scan}", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NoSnapshotVariants))]
    public void Index_WithoutSnapshot_NamesScanInJson(string[] args)
    {
        var (exitCode, stdout, _) = _harness.Invoke([.. args, "--json"]);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("NoEnvironmentSnapshot", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Scan, error.GetProperty("hint").GetString());
    }
}
