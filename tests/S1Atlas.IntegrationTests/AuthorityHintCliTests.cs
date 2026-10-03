using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests;

internal sealed class HintCliHarness : IAsyncDisposable
{
    public string DataRoot { get; } = Path.Combine(
        Path.GetTempPath(), "s1atlas-hint-" + Guid.NewGuid().ToString("N"));

    public HintCliHarness()
    {
        Directory.CreateDirectory(DataRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(DataRoot);
    }

    public async Task SeedAsync(Func<SqliteAtlasRepository, Task> seed)
    {
        using var repository = new SqliteAtlasRepository(
            Path.Combine(DataRoot, "atlas.db"),
            Path.Combine(DataRoot, "backups"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        await seed(repository);
    }

    public (int ExitCode, string Stdout, string Stderr) Invoke(params string[] args)
    {
        var application = new CliApplication(DataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }
}

public sealed class AuthorityHintCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-16T00:00:00Z");

    private const string ToolInstanceId = "tool-instance-1";
    private const string BuildId = "build-hint";
    private const string OtherBuildId =
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
    private const string RecipeId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly HintCliHarness _harness = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Search_NoCurrentBuild_NamesScanInBothFormats()
    {
        await AssertHumanHintAsync(
            [],
            "NoCurrentBuild",
            ReadinessFixCommands.Scan);
        await AssertJsonHintAsync(
            [],
            "NoCurrentBuild",
            ReadinessFixCommands.Scan);
    }

    [Fact]
    public async Task Search_UnknownBuild_NamesBuildsInBothFormats()
    {
        await AssertHumanHintAsync(
            ["search", "Widget", "--build", "missing-build"],
            "BuildNotFound",
            ReadinessFixCommands.Builds);
        await AssertJsonHintAsync(
            ["search", "Widget", "--build", "missing-build"],
            "BuildNotFound",
            ReadinessFixCommands.Builds);
    }

    [Fact]
    public async Task Search_AmbiguousBuildPrefix_OmitsHintInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            var ct = TestContext.Current.CancellationToken;
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot("abcdef12" + new string('0', 56), BaseTime), ct);
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot("abcdef12" + new string('1', 56), BaseTime), ct);
        });

        await AssertHumanHintAsync(
            ["search", "Widget", "--build", "abcdef12"],
            "AmbiguousBuildPrefix",
            null);
        await AssertJsonHintAsync(
            ["search", "Widget", "--build", "abcdef12"],
            "AmbiguousBuildPrefix",
            null);
    }

    [Fact]
    public async Task Search_SnapshotWithoutExtraction_NamesExtractInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot(BuildId, BaseTime),
                TestContext.Current.CancellationToken);
        });

        await AssertHumanHintAsync([], "NoPreferredVerifiedExtraction", ReadinessFixCommands.Extract);
        await AssertJsonHintAsync([], "NoPreferredVerifiedExtraction", ReadinessFixCommands.Extract);
    }

    [Fact]
    public async Task Search_CorruptedPreferredExtraction_OmitsHintInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            var ct = TestContext.Current.CancellationToken;
            var extractionId = await SeedPreferredExtractionAsync(repository, BuildId);
            var validationPath = Path.Combine(
                _harness.DataRoot, "builds", BuildId, "extractions", extractionId, "validation.json");
            await File.AppendAllTextAsync(validationPath, "\n", ct);
        });

        await AssertHumanHintAsync(
            [],
            "ExtractionIntegrityFailure",
            null);
        await AssertJsonHintAsync(
            [],
            "ExtractionIntegrityFailure",
            null);
    }

    [Fact]
    public async Task Search_NonCurrentBuildWithoutExtraction_TargetsHintAtRequestedBuild()
    {
        await _harness.SeedAsync(async repository =>
        {
            var ct = TestContext.Current.CancellationToken;
            await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(OtherBuildId, BaseTime), ct);
            await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildId, BaseTime), ct);
        });

        const string expected = "s1atlas extract --build abcdef123456";
        await AssertHumanHintAsync(
            ["search", "Widget", "--build", OtherBuildId],
            "NoPreferredVerifiedExtraction",
            expected);
        await AssertJsonHintAsync(
            ["search", "Widget", "--build", OtherBuildId],
            "NoPreferredVerifiedExtraction",
            expected);
    }

    [Fact]
    public async Task Search_NonCurrentBuildWithoutIndex_OmitsHint()
    {
        await _harness.SeedAsync(async repository =>
        {
            var ct = TestContext.Current.CancellationToken;
            await SeedPreferredExtractionAsync(repository, OtherBuildId);
            await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildId, BaseTime), ct);
        });

        await AssertHumanHintAsync(
            ["search", "Widget", "--build", OtherBuildId],
            "NoCompletedIndex",
            null);
        await AssertJsonHintAsync(
            ["search", "Widget", "--build", OtherBuildId],
            "NoCompletedIndex",
            null);
    }

    [Fact]
    public async Task Search_PreferredExtractionWithoutIndex_NamesIndexInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            await SeedPreferredExtractionAsync(repository, BuildId);
        });

        await AssertHumanHintAsync([], "NoCompletedIndex", ReadinessFixCommands.Index);
        await AssertJsonHintAsync([], "NoCompletedIndex", ReadinessFixCommands.Index);
    }

    [Fact]
    public async Task Search_IndexBoundToAnotherBuild_OmitsHintInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            var ct = TestContext.Current.CancellationToken;
            var seeded = await SeedHealthyBuildAsync(repository, BuildId);
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot("build-other", BaseTime), ct);
            await ExecuteAsync(
                $"UPDATE code_snapshots SET environment_snapshot_id = (" +
                $"SELECT snapshot_id FROM environment_snapshots WHERE build_id = 'build-other' LIMIT 1) " +
                $"WHERE snapshot_id = 'snapshot-{seeded}';");
            await repository.SaveSnapshotAsync(
                ExtractionSeed.CreateSnapshot(BuildId, BaseTime), ct);
        });

        await AssertHumanHintAsync([], "IndexBuildMismatch", null);
        await AssertJsonHintAsync([], "IndexBuildMismatch", null);
    }

    [Fact]
    public async Task Search_MissingApiIndex_NamesInstalledApiIndexInBothFormats()
    {
        await _harness.SeedAsync(async repository =>
        {
            await SeedHealthyBuildAsync(repository, BuildId);
        });

        await AssertHumanHintAsync(
            ["search", "Widget", "--codebase", "s1api", "--channel", "installed"],
            "NoCompletedIndex",
            ReadinessFixCommands.IndexApiInstalled(CodebaseKind.S1Api));
        await AssertJsonHintAsync(
            ["search", "Widget", "--codebase", "s1api", "--channel", "installed"],
            "NoCompletedIndex",
            ReadinessFixCommands.IndexApiInstalled(CodebaseKind.S1Api));
    }

    [Fact]
    public async Task Search_MissingReferenceCollection_OmitsHint()
    {
        await _harness.SeedAsync(async repository =>
        {
            await SeedHealthyBuildAsync(repository, BuildId);
        });

        var (exitCode, _, stderr) = _harness.Invoke(
            "search", "Widget", "--scope", "reference", "--collection", "qol");

        Assert.Equal(1, exitCode);
        Assert.Contains("Code:    NoCompletedIndex", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Next:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_NoCurrentBuild_NamesScan()
    {
        var (exitCode, _, stderr) = _harness.Invoke("source", "Widget");

        Assert.Equal(1, exitCode);
        Assert.Contains("Code:    NoCurrentBuild", stderr, StringComparison.Ordinal);
        Assert.Contains($"Next:    {ReadinessFixCommands.Scan}", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_NoCurrentBuild_NamesScan()
    {
        var (exitCode, _, stderr) = _harness.Invoke("open", "Widget");

        Assert.Equal(1, exitCode);
        Assert.Contains("Code:    NoCurrentBuild", stderr, StringComparison.Ordinal);
        Assert.Contains($"Next:    {ReadinessFixCommands.Scan}", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverNativeBody_NoCurrentBuild_NamesScan()
    {
        var (exitCode, _, stderr) = _harness.Invoke(
            "recover-native-body", "--symbol-id", "deadbeef");

        Assert.Equal(1, exitCode);
        Assert.Contains("Code:    NoCurrentBuild", stderr, StringComparison.Ordinal);
        Assert.Contains($"Next:    {ReadinessFixCommands.Scan}", stderr, StringComparison.Ordinal);
    }

    private async Task AssertHumanHintAsync(
        string[] args,
        string expectedCode,
        string? expectedHint)
    {
        await Task.CompletedTask;
        string[] fullArgs = args.Length == 0 ? ["search", "Widget"] : args;
        var (exitCode, _, stderr) = _harness.Invoke(fullArgs);

        Assert.Equal(1, exitCode);
        Assert.Contains($"Code:    {expectedCode}", stderr, StringComparison.Ordinal);
        if (expectedHint is null)
        {
            Assert.DoesNotContain("Next:", stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Next:    {expectedHint}", stderr, StringComparison.Ordinal);
        }
    }

    private async Task AssertJsonHintAsync(
        string[] args,
        string expectedCode,
        string? expectedHint)
    {
        await Task.CompletedTask;
        string[] fullArgs = args.Length == 0
            ? ["search", "Widget", "--json"]
            : [.. args, "--json"];
        var (exitCode, stdout, _) = _harness.Invoke(fullArgs);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        if (expectedHint is null)
        {
            Assert.False(error.TryGetProperty("hint", out _), stdout);
        }
        else
        {
            Assert.Equal(expectedHint, error.GetProperty("hint").GetString());
        }
    }

    private async Task<string> SeedPreferredExtractionAsync(
        SqliteAtlasRepository repository,
        string buildId)
    {
        var ct = TestContext.Current.CancellationToken;
        await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(buildId, BaseTime), ct);
        await ExtractionSeed.SeedToolInstanceAsync(_harness.DataRoot, ToolInstanceId, ct);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository,
            _harness.DataRoot,
            buildId,
            RecipeId,
            ToolInstanceId,
            ProfileDigest,
            PolicyDigest,
            BaseTime,
            ct);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            ct);
        return seeded.Extraction.ExtractionId;
    }

    private async Task<string> SeedHealthyBuildAsync(
        SqliteAtlasRepository repository,
        string buildId)
    {
        var ct = TestContext.Current.CancellationToken;
        var extractionId = await SeedPreferredExtractionAsync(repository, buildId);
        var snapshotId = "snapshot-" + extractionId;
        var indexId = "index-" + extractionId;
        const string createdAtUtc = "2026-08-16T00:20:00.0000000+00:00";
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                extractionId,
                createdAtUtc),
            ct);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, createdAtUtc),
            ct);
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [
                    new IndexSymbolRecord(
                        "symbol-" + extractionId,
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.Widget",
                        "Type",
                        "Demo.Widget",
                        "Demo.Widget",
                        false)
                ],
                [],
                [],
                [],
                []),
            "2026-08-16T00:21:00.0000000+00:00",
            ct);
        return extractionId;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_harness.DataRoot, "atlas.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
