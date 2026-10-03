using System.CommandLine;
using System.Text.Json;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli;
using S1Atlas.Cli.Commands;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class StatusReadinessTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "s1atlas-status-" + Guid.NewGuid().ToString("N"));

    public StatusReadinessTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }

    [Fact]
    public async Task Human_WithSnapshot_AppendsReadySummary()
    {
        using var repository = await CreateRepositoryAsync(seeded: true);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = StatusCommand.Create(
            repository,
            new ScriptedReadinessService(ReadinessReportBuilder.Ready()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeStatus(command, ["status"]);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Current build: build-status", lines, StringComparer.Ordinal);
        Assert.Equal("Ready", lines[^1]);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Human_EmptyRoot_AppendsNextSummary()
    {
        using var repository = await CreateRepositoryAsync(seeded: false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = StatusCommand.Create(
            repository,
            new ScriptedReadinessService(ReadinessReportBuilder.Blocked()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeStatus(command, ["status"]);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("No indexed builds. Run 's1atlas scan'.", lines[0]);
        Assert.Equal("Next: s1atlas tools install cpp2il", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task Json_IncludesReadinessObjectWithoutChangingFields()
    {
        using var repository = await CreateRepositoryAsync(seeded: true);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = StatusCommand.Create(
            repository,
            new ScriptedReadinessService(ReadinessReportBuilder.Blocked()),
            output,
            error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeStatus(command, ["status", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("hasCurrentBuild").GetBoolean());
        Assert.Equal("build-status", data.GetProperty("buildId").GetString());
        var readiness = data.GetProperty("readiness");
        Assert.False(readiness.GetProperty("isReady").GetBoolean());
        Assert.Equal(
            "Next: s1atlas tools install cpp2il",
            readiness.GetProperty("summary").GetString());
        Assert.Equal(
            "s1atlas tools install cpp2il",
            readiness.GetProperty("nextCommand").GetString());
    }

    [Fact]
    public void FullApp_EmptyRoot_StatusShowsNextStep()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Next:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FullApp_EmptyRoot_StatusJsonIncludesReadiness()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status", "--json"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var readiness = document.RootElement.GetProperty("data").GetProperty("readiness");
        Assert.False(readiness.GetProperty("isReady").GetBoolean());
        Assert.StartsWith("Next:", readiness.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    private async Task<SqliteAtlasRepository> CreateRepositoryAsync(bool seeded)
    {
        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"),
            Path.Combine(_dataDirectory, "backups"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        if (seeded)
        {
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot(
                    "build-status",
                    DateTimeOffset.Parse("2026-08-16T00:00:00Z")),
                TestContext.Current.CancellationToken);
        }

        return repository;
    }

    private static int InvokeStatus(Command command, string[] args)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(command);
        return root.Parse(args).Invoke();
    }
}
