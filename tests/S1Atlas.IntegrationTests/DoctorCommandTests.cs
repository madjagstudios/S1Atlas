using System.CommandLine;
using System.Text;
using System.Text.Json;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli;
using S1Atlas.Cli.Commands;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests;

internal sealed class ScriptedReadinessService(ReadinessReport report) : IAtlasReadinessService
{
    public int EvaluationCount { get; private set; }

    public Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken)
    {
        EvaluationCount++;
        return Task.FromResult(report);
    }
}

internal static class ReadinessReportBuilder
{
    public static ReadinessReport Blocked() =>
        new(
            [
                new ReadinessItem(
                    ReadinessItemIds.Scan, "Build scan",
                    ReadinessState.Ok, "Build scanned.", null, false),
                new ReadinessItem(
                    ReadinessItemIds.Tools, "Managed tools",
                    ReadinessState.Missing, "cpp2il is not installed.",
                    "s1atlas tools install cpp2il", false),
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Stale, "The preferred extraction failed integrity verification.",
                    "s1atlas extract --retry", false),
                new ReadinessItem(
                    ReadinessItemIds.Scene, "Scene snapshot (optional)",
                    ReadinessState.NotApplicable, "Optional; needs a scanned build first.",
                    "s1atlas index --scene", true)
            ],
            new ReadinessNextStep(false, "Next: s1atlas tools install cpp2il", "s1atlas tools install cpp2il"),
            false,
            ReadinessFixCommands.ExampleQuery,
            ["cpp2il"]);

    public static ReadinessReport Ready() =>
        new(
            [
                new ReadinessItem(
                    ReadinessItemIds.Scan, "Build scan",
                    ReadinessState.Ok, "Build scanned.", null, false)
            ],
            new ReadinessNextStep(true, "Ready", ReadinessFixCommands.ExampleQuery),
            true,
            ReadinessFixCommands.ExampleQuery,
            []);
}

public sealed class DoctorCommandTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "s1atlas-doctor-" + Guid.NewGuid().ToString("N"));

    public DoctorCommandTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }

    [Fact]
    public void Human_RendersGlyphChecklistPlusNextStep()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = DoctorCommand.Create(
            new ScriptedReadinessService(ReadinessReportBuilder.Blocked()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeDoctor(command, ["doctor"]);

        Assert.Equal(1, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("✓ Build scan: Build scanned.", lines[0]);
        Assert.Equal("✗ Managed tools: cpp2il is not installed.", lines[1]);
        Assert.Equal("✗ Preferred extraction: The preferred extraction failed integrity verification.", lines[2]);
        Assert.Equal("- Scene snapshot (optional): Optional; needs a scanned build first.", lines[3]);
        Assert.Equal("Next: s1atlas tools install cpp2il", lines[4]);
        Assert.Equal(5, lines.Length);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Human_FallsBackToBracketMarksOnNonUtf8Writer()
    {
        using var stream = new MemoryStream();
        using var output = new StreamWriter(stream, Encoding.Latin1);
        using var error = new StringWriter();
        var command = DoctorCommand.Create(
            new ScriptedReadinessService(ReadinessReportBuilder.Blocked()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeDoctor(command, ["doctor"]);
        output.Flush();

        Assert.Equal(1, exitCode);
        var text = Encoding.Latin1.GetString(stream.ToArray());
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[ok] Build scan: Build scanned.", lines[0]);
        Assert.Equal("[missing] Managed tools: cpp2il is not installed.", lines[1]);
        Assert.Equal("[stale] Preferred extraction: The preferred extraction failed integrity verification.", lines[2]);
        Assert.Equal("[n/a] Scene snapshot (optional): Optional; needs a scanned build first.", lines[3]);
    }

    [Fact]
    public void Human_Ready_PrintsExampleQueryAndExitsZero()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = DoctorCommand.Create(
            new ScriptedReadinessService(ReadinessReportBuilder.Ready()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeDoctor(command, ["doctor"]);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("✓ Build scan: Build scanned.", lines[0]);
        Assert.Equal("Ready", lines[1]);
        Assert.Equal($"Example query: {ReadinessFixCommands.ExampleQuery}", lines[2]);
    }

    [Fact]
    public void Json_WritesChecklistShape()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = DoctorCommand.Create(
            new ScriptedReadinessService(ReadinessReportBuilder.Blocked()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeDoctor(command, ["doctor", "--json"]);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("doctor", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        var data = root.GetProperty("data");
        Assert.False(data.GetProperty("isReady").GetBoolean());
        var nextStep = data.GetProperty("nextStep");
        Assert.Equal("Next: s1atlas tools install cpp2il", nextStep.GetProperty("summary").GetString());
        Assert.Equal("s1atlas tools install cpp2il", nextStep.GetProperty("command").GetString());
        var items = data.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal("scan", items[0].GetProperty("id").GetString());
        Assert.Equal("ok", items[0].GetProperty("state").GetString());
        Assert.False(items[0].TryGetProperty("fixCommand", out _));
        Assert.Equal("missing", items[1].GetProperty("state").GetString());
        Assert.Equal(
            "s1atlas tools install cpp2il",
            items[1].GetProperty("fixCommand").GetString());
        Assert.Equal("stale", items[2].GetProperty("state").GetString());
        Assert.Equal("not-applicable", items[3].GetProperty("state").GetString());
        Assert.True(items[3].GetProperty("optional").GetBoolean());
    }

    [Fact]
    public void FullApp_EmptyRoot_ExitsOneWithoutCreatingFiles()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["doctor"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Build scan", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Next:", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _dataDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FullApp_LockedDatabase_ShowsUnreadableWording()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SchemaVersionFixtures.UpgradeToCurrentAsync(
            Path.Combine(_dataDirectory, "atlas.db"),
            Path.Combine(_dataDirectory, "backups"),
            cancellationToken);

        using var lockHandle = new FileStream(
            Path.Combine(_dataDirectory, "atlas.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["doctor"], output, error, cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("could not be read", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Back up and remove", output.ToString(), StringComparison.Ordinal);
    }

    private static int InvokeDoctor(Command command, string[] args)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(command);
        return root.Parse(args).Invoke();
    }
}
