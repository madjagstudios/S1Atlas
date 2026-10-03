using S1Atlas.Application.Readiness;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Readiness;

public sealed class AtlasReadinessServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    private const string BuildId = "build-readiness";
    private const string SteamBuildId = "steam-build-1";
    private const string ExecutableVersion = "2022.3.62f1";
    private const string OldCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _gameDir;
    private readonly string _gameAssemblyPath;
    private readonly string _globalMetadataPath;

    public AtlasReadinessServiceTests()
    {
        _gameDir = Path.Combine(
            Path.GetTempPath(), "s1atlas-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_gameDir);
        _gameAssemblyPath = Path.Combine(_gameDir, "GameAssembly.dll");
        _globalMetadataPath = Path.Combine(_gameDir, "global-metadata.dat");
        File.WriteAllBytes(_gameAssemblyPath, [10, 20, 30, 40, 50, 60]);
        File.WriteAllBytes(_globalMetadataPath, [1, 2, 3, 4]);
        FreezeGameFiles(CapturedAt.AddHours(-1));
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_gameDir);
    }

    [Fact]
    public async Task Evaluate_EmptyRoot_NextStepIsScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.False(report.IsReady);
        Assert.Equal("Next: s1atlas scan", report.NextStep.Summary);
        Assert.Equal(ReadinessFixCommands.Scan, report.NextStep.Command);
        Assert.Equal(ReadinessState.Missing, Item(report, ReadinessItemIds.Scan).State);
        Assert.Equal(ReadinessFixCommands.Scan, Item(report, ReadinessItemIds.Scan).FixCommand);
    }

    [Fact]
    public async Task Evaluate_Always_ReturnsTenItemsInPipelineOrder()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(
            new[]
            {
                ReadinessItemIds.AtlasSchema,
                ReadinessItemIds.DotNetRuntime,
                ReadinessItemIds.GameInstall,
                ReadinessItemIds.Scan,
                ReadinessItemIds.Tools,
                ReadinessItemIds.Extraction,
                ReadinessItemIds.Index,
                ReadinessItemIds.Scene,
                ReadinessItemIds.ApiIndex,
                ReadinessItemIds.ReferenceCollections
            },
            report.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Evaluate_OptionalItems_AreFlaggedOptional()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        foreach (var item in report.Items)
        {
            var expectedOptional = item.Id is
                ReadinessItemIds.Scene or
                ReadinessItemIds.ApiIndex or
                ReadinessItemIds.ReferenceCollections;
            Assert.Equal(expectedOptional, item.IsOptional);
            if (expectedOptional)
            {
                Assert.Contains("optional", item.Title, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Evaluate_MissingSchema_ReportsNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            schema: new AtlasSchemaStatus(AtlasSchemaStatusKind.NotCreated, null, 5));

        var report = await service.EvaluateAsync(CancellationToken.None);

        var schema = Item(report, ReadinessItemIds.AtlasSchema);
        Assert.Equal(ReadinessState.NotApplicable, schema.State);
        Assert.Contains("No atlas database", schema.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_CurrentSchema_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var schema = Item(report, ReadinessItemIds.AtlasSchema);
        Assert.Equal(ReadinessState.Ok, schema.State);
        Assert.Null(schema.FixCommand);
    }

    [Fact]
    public async Task Evaluate_BehindSchema_BlocksWithStatusFixAndSkipsDatabaseReads()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var seeded = await harness.SeedHealthyInstalledBuildAsync(BuildId);
        var service = CreateService(
            harness,
            schema: new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 3, 5));

        var report = await service.EvaluateAsync(CancellationToken.None);

        var schema = Item(report, ReadinessItemIds.AtlasSchema);
        Assert.Equal(ReadinessState.Stale, schema.State);
        Assert.Equal(ReadinessFixCommands.Status, schema.FixCommand);
        Assert.Contains("v3", schema.Detail, StringComparison.Ordinal);
        Assert.Contains("v5", schema.Detail, StringComparison.Ordinal);
        Assert.False(report.IsReady);
        Assert.Equal("Next: s1atlas status", report.NextStep.Summary);
        Assert.Equal(ReadinessFixCommands.Status, report.NextStep.Command);
        Assert.Equal(
            ReadinessState.NotApplicable,
            Item(report, ReadinessItemIds.Extraction).State);
        Assert.Equal(ReadinessState.NotApplicable, Item(report, ReadinessItemIds.Index).State);
        Assert.Equal(ReadinessState.NotApplicable, Item(report, ReadinessItemIds.Scan).State);
    }

    [Fact]
    public async Task Evaluate_AheadSchema_BlocksWithoutFixCommand()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            schema: new AtlasSchemaStatus(AtlasSchemaStatusKind.Ahead, 9, 5));

        var report = await service.EvaluateAsync(CancellationToken.None);

        var schema = Item(report, ReadinessItemIds.AtlasSchema);
        Assert.Equal(ReadinessState.Missing, schema.State);
        Assert.Null(schema.FixCommand);
        Assert.Contains("v9", schema.Detail, StringComparison.Ordinal);
        Assert.False(report.IsReady);
        Assert.Null(report.NextStep.Command);
        Assert.StartsWith("Next: ", report.NextStep.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_UnrecognizedSchema_BlocksWithoutFixCommand()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            schema: new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, 5));

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Missing, Item(report, ReadinessItemIds.AtlasSchema).State);
        Assert.False(report.IsReady);
    }

    [Fact]
    public async Task Evaluate_UnsupportedRuntime_BlocksWithoutFixCommand()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness, runtime: ReadinessFixtures.UnsupportedRuntime);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var runtime = Item(report, ReadinessItemIds.DotNetRuntime);
        Assert.Equal(ReadinessState.Missing, runtime.State);
        Assert.Null(runtime.FixCommand);
        Assert.False(report.IsReady);
        Assert.Equal($"Next: {runtime.Detail}", report.NextStep.Summary);
        Assert.Null(report.NextStep.Command);
    }

    [Fact]
    public async Task Evaluate_MissingGame_BlocksWithoutFixCommand()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness, noGame: true);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var game = Item(report, ReadinessItemIds.GameInstall);
        Assert.Equal(ReadinessState.Missing, game.State);
        Assert.Null(game.FixCommand);
        Assert.Contains("Schedule I", game.Detail, StringComparison.Ordinal);
        Assert.False(report.IsReady);
        Assert.Equal($"Next: {game.Detail}", report.NextStep.Summary);
    }

    [Fact]
    public async Task Evaluate_MatchingLiveInstall_ReportsScanOkWithInputSizes()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Ok, scan.State);
        Assert.Contains("unmodified since scan", scan.Detail, StringComparison.Ordinal);
        Assert.Contains("6 B", scan.Detail, StringComparison.Ordinal);
        Assert.Contains("4 B", scan.Detail, StringComparison.Ordinal);
        Assert.Contains(SteamBuildId, scan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_SteamBuildChanged_ReportsStaleScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            TestSnapshot(steamBuildId: "steam-build-old"),
            CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Equal(ReadinessFixCommands.Scan, scan.FixCommand);
        Assert.Contains("steam-build-old", scan.Detail, StringComparison.Ordinal);
        Assert.Contains(SteamBuildId, scan.Detail, StringComparison.Ordinal);
        Assert.False(report.IsReady);
        Assert.Equal(ReadinessFixCommands.Scan, report.NextStep.Command);
    }

    [Fact]
    public async Task Evaluate_ExecutableVersionChanged_ReportsStaleScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            TestSnapshot(executableVersion: "2021.3.1f1"),
            CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains("2021.3.1f1", scan.Detail, StringComparison.Ordinal);
        Assert.Contains(ExecutableVersion, scan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_GameFilesModifiedAfterCapture_ReportsStaleScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        File.SetLastWriteTimeUtc(_gameAssemblyPath, CapturedAt.AddHours(1).UtcDateTime);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains("modified after the scan", scan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_InstallationMoved_ReportsStaleScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            TestSnapshot(installationRoot: Path.Combine(Path.GetTempPath(), "s1atlas-old-game")),
            CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Stale, Item(report, ReadinessItemIds.Scan).State);
    }

    [Fact]
    public async Task Evaluate_SnapshotWithoutLiveGame_ReportsStaleScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        var service = CreateService(harness, noGame: true);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Stale, Item(report, ReadinessItemIds.Scan).State);
        Assert.Equal(ReadinessState.Missing, Item(report, ReadinessItemIds.GameInstall).State);
    }

    [Fact]
    public async Task Evaluate_VerifiedTools_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.Tools).State);
        Assert.Empty(report.MissingRequiredToolIds);
    }

    [Fact]
    public async Task Evaluate_MissingCpp2Il_NamesCpp2IlInstallFirst()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            tools:
            [
                ReadinessFixtures.UnverifiedTool("cpp2il", ToolInstallationStatus.NotInstalled),
                ReadinessFixtures.VerifiedTool("unity-classdata")
            ]);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var tools = Item(report, ReadinessItemIds.Tools);
        Assert.Equal(ReadinessState.Missing, tools.State);
        Assert.Equal("s1atlas tools install cpp2il", tools.FixCommand);
        Assert.Equal(["cpp2il"], report.MissingRequiredToolIds);
    }

    [Fact]
    public async Task Evaluate_BothToolsMissing_ListsBothInPipelineOrder()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            tools:
            [
                ReadinessFixtures.UnverifiedTool("unity-classdata", ToolInstallationStatus.NotInstalled),
                ReadinessFixtures.UnverifiedTool("cpp2il", ToolInstallationStatus.NotInstalled)
            ]);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal("s1atlas tools install cpp2il", Item(report, ReadinessItemIds.Tools).FixCommand);
        Assert.Equal(["cpp2il", "unity-classdata"], report.MissingRequiredToolIds);
    }

    [Fact]
    public async Task Evaluate_CorruptClassData_NamesClassDataInstall()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(
            harness,
            tools:
            [
                ReadinessFixtures.VerifiedTool("cpp2il"),
                ReadinessFixtures.UnverifiedTool("unity-classdata", ToolInstallationStatus.Corrupt)
            ]);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(
            "s1atlas tools install unity-classdata",
            Item(report, ReadinessItemIds.Tools).FixCommand);
    }

    [Fact]
    public async Task Evaluate_UnreadableToolDefinitions_ReportsMissingWithoutFix()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateServiceWithToolReader(
            harness,
            new ThrowingToolReader(new ToolOperationException(
                "ToolDefinitionInvalid", "Tool definition directory is missing.")));

        var report = await service.EvaluateAsync(CancellationToken.None);

        var tools = Item(report, ReadinessItemIds.Tools);
        Assert.Equal(ReadinessState.Missing, tools.State);
        Assert.Null(tools.FixCommand);
        Assert.Contains("tool definitions", tools.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_NoSnapshot_ReportsExtractionNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var extraction = Item(report, ReadinessItemIds.Extraction);
        Assert.Equal(ReadinessState.NotApplicable, extraction.State);
        Assert.Equal(ReadinessFixCommands.Extract, extraction.FixCommand);
    }

    [Fact]
    public async Task Evaluate_SnapshotWithoutExtraction_ReportsExtractionMissing()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var extraction = Item(report, ReadinessItemIds.Extraction);
        Assert.Equal(ReadinessState.Missing, extraction.State);
        Assert.Equal(ReadinessFixCommands.Extract, extraction.FixCommand);
    }

    [Fact]
    public async Task Evaluate_CorruptPreferredExtraction_ReportsGuidanceWithoutFixCommand()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCorruptedPreferenceAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var extraction = Item(report, ReadinessItemIds.Extraction);
        Assert.Equal(ReadinessState.Missing, extraction.State);
        Assert.Null(extraction.FixCommand);
        Assert.Contains("integrity", extraction.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No command rebuilds it in place", extraction.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_VerifiedExtraction_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedHealthyInstalledBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.Extraction).State);
    }

    [Fact]
    public async Task Evaluate_NoExtraction_ReportsIndexNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var index = Item(report, ReadinessItemIds.Index);
        Assert.Equal(ReadinessState.NotApplicable, index.State);
        Assert.Equal(ReadinessFixCommands.Index, index.FixCommand);
    }

    [Fact]
    public async Task Evaluate_ExtractionWithoutIndex_ReportsIndexMissing()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedPreferredVerifiedExtractionAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var index = Item(report, ReadinessItemIds.Index);
        Assert.Equal(ReadinessState.Missing, index.State);
        Assert.Equal(ReadinessFixCommands.Index, index.FixCommand);
    }

    [Fact]
    public async Task Evaluate_CompletedIndex_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedHealthyInstalledBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.Index).State);
    }

    [Fact]
    public async Task Evaluate_IndexBoundToAnotherBuild_ReportsMissingWithForceFix()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync("build-a");
        await harness.SeedCompletedInstalledIndexAssociatedWithDifferentBuildAsync(extractionId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var index = Item(report, ReadinessItemIds.Index);
        Assert.Equal(ReadinessState.Missing, index.State);
        Assert.Equal(ReadinessFixCommands.IndexForce, index.FixCommand);
    }

    [Fact]
    public async Task Evaluate_NoSnapshot_ReportsSceneNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.NotApplicable, Item(report, ReadinessItemIds.Scene).State);
    }

    [Fact]
    public async Task Evaluate_SnapshotWithoutScene_ReportsSceneMissing()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scene = Item(report, ReadinessItemIds.Scene);
        Assert.Equal(ReadinessState.Missing, scene.State);
        Assert.Equal(ReadinessFixCommands.IndexScene, scene.FixCommand);
        Assert.True(scene.IsOptional);
    }

    [Fact]
    public async Task Evaluate_PublishedSceneSnapshot_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var seeded = await harness.SeedHealthyInstalledBuildAsync(BuildId);
        await ReadinessFixtures.SeedCompletedSceneSnapshotAsync(
            harness.Repository, harness.DatabasePath, seeded.BuildId, "scene-readiness",
            seeded.ExtractionId, "snapshot-" + seeded.ExtractionId, seeded.IndexId,
            TestContext.Current.CancellationToken);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.Scene).State);
    }

    [Fact]
    public async Task Evaluate_NoSnapshot_ReportsApiNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.NotApplicable, Item(report, ReadinessItemIds.ApiIndex).State);
    }

    [Fact]
    public async Task Evaluate_SnapshotWithoutApiIndexes_ReportsApiMissing()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var api = Item(report, ReadinessItemIds.ApiIndex);
        Assert.Equal(ReadinessState.Missing, api.State);
        Assert.Equal(
            ReadinessFixCommands.IndexApiInstalled(CodebaseKind.S1Api),
            api.FixCommand);
    }

    [Fact]
    public async Task Evaluate_InstalledApiIndexes_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        await SeedInstalledApiIndexesAsync(harness);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.ApiIndex).State);
    }

    [Fact]
    public async Task Evaluate_ReleaseIndexOlderThanCachedCommit_ReportsApiStale()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        await SeedInstalledApiIndexesAsync(harness);
        await ReadinessFixtures.SeedCompletedCodeIndexAsync(
            harness.Repository,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            OldCommit,
            environmentSnapshotId: null,
            "index-s1api-release",
            "snapshot-s1api-release",
            TestContext.Current.CancellationToken);
        var service = CreateService(
            harness,
            upstream: new Dictionary<CodebaseKind, IReadOnlyList<string>>
            {
                [CodebaseKind.S1Api] = [NewCommit]
            });

        var report = await service.EvaluateAsync(CancellationToken.None);

        var api = Item(report, ReadinessItemIds.ApiIndex);
        Assert.Equal(ReadinessState.Stale, api.State);
        Assert.Equal(
            ReadinessFixCommands.IndexApiCommit(CodebaseKind.S1Api, CodeChannel.Release, NewCommit),
            api.FixCommand);
    }

    [Fact]
    public async Task Evaluate_CachedCommitWithoutReleaseIndex_KeepsApiOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        await SeedInstalledApiIndexesAsync(harness);
        var service = CreateService(
            harness,
            upstream: new Dictionary<CodebaseKind, IReadOnlyList<string>>
            {
                [CodebaseKind.S1Api] = [NewCommit]
            });

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.ApiIndex).State);
    }

    [Fact]
    public async Task Evaluate_IdentityV1Snapshot_ReportsApiNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = harness.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var downgrade = connection.CreateCommand();
        downgrade.CommandText = "UPDATE environment_snapshots SET identity_version = 1;";
        await downgrade.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var api = Item(report, ReadinessItemIds.ApiIndex);
        Assert.Equal(ReadinessState.NotApplicable, api.State);
        Assert.Equal(ReadinessFixCommands.Scan, api.FixCommand);
    }

    [Fact]
    public async Task Evaluate_NoSnapshot_ReportsReferenceNotApplicable()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(
            ReadinessState.NotApplicable,
            Item(report, ReadinessItemIds.ReferenceCollections).State);
    }

    [Fact]
    public async Task Evaluate_NoReferenceIndexes_ReportsMissingWithoutFix()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync(BuildId);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var reference = Item(report, ReadinessItemIds.ReferenceCollections);
        Assert.Equal(ReadinessState.Missing, reference.State);
        Assert.Null(reference.FixCommand);
        Assert.True(reference.IsOptional);
        Assert.Contains("reference index", reference.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_CompletedReferenceIndex_ReportsOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var seeded = await harness.SeedHealthyInstalledBuildAsync(BuildId);
        await ReadinessFixtures.SeedCompletedReferenceIndexAsync(
            harness.Repository, "qol", "index-reference", "snapshot-reference",
            seeded.BuildId, seeded.IndexId, TestContext.Current.CancellationToken);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ReadinessState.Ok, Item(report, ReadinessItemIds.ReferenceCollections).State);
    }

    [Fact]
    public async Task Evaluate_NextStep_IsFirstRequiredItemThatIsNotOk()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        var service = CreateService(
            harness,
            tools:
            [
                ReadinessFixtures.UnverifiedTool("cpp2il", ToolInstallationStatus.NotInstalled),
                ReadinessFixtures.VerifiedTool("unity-classdata")
            ]);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.False(report.IsReady);
        Assert.Equal("Next: s1atlas tools install cpp2il", report.NextStep.Summary);
        Assert.Equal("s1atlas tools install cpp2il", report.NextStep.Command);
    }

    [Fact]
    public async Task Evaluate_FullyReady_ReportsReadyWithExampleQuery()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await SeedFullyReadyAsync(harness);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.True(report.IsReady);
        Assert.True(report.NextStep.IsReady);
        Assert.Equal("Ready", report.NextStep.Summary);
        Assert.Equal(ReadinessFixCommands.ExampleQuery, report.NextStep.Command);
        Assert.Equal(ReadinessFixCommands.ExampleQuery, report.ExampleQuery);
        Assert.All(report.Items, item => Assert.Equal(ReadinessState.Ok, item.State));
    }

    [Fact]
    public async Task Evaluate_OptionalItemsMissing_StillReportsReady()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync(BuildId);
        await harness.SeedCompletedInstalledIndexAsync(extractionId, BuildId);
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        var service = CreateService(harness);

        var report = await service.EvaluateAsync(CancellationToken.None);

        Assert.True(report.IsReady);
        Assert.Equal(ReadinessState.Missing, Item(report, ReadinessItemIds.Scene).State);
        Assert.Equal(ReadinessState.Missing, Item(report, ReadinessItemIds.ApiIndex).State);
        Assert.Equal(
            ReadinessState.Missing,
            Item(report, ReadinessItemIds.ReferenceCollections).State);
    }

    private static ReadinessItem Item(ReadinessReport report, string id) =>
        report.Items.Single(item => item.Id == id);

    private void FreezeGameFiles(DateTimeOffset timestamp)
    {
        File.SetLastWriteTimeUtc(_gameAssemblyPath, timestamp.UtcDateTime);
        File.SetLastWriteTimeUtc(_globalMetadataPath, timestamp.UtcDateTime);
    }

    private ScheduleOneInstallation GameInstall() =>
        new(
            _gameDir,
            Path.Combine(_gameDir, "Schedule I.exe"),
            _gameAssemblyPath,
            _globalMetadataPath,
            Path.Combine(_gameDir, "Mods"),
            Path.Combine(_gameDir, "MelonLoader"));

    private InstallationObservation LiveObservation() =>
        new(
            ExecutableVersion,
            "3164500",
            SteamBuildId,
            _gameDir,
            _gameAssemblyPath,
            _globalMetadataPath);

    private EnvironmentSnapshot TestSnapshot(
        string? steamBuildId = null,
        string? executableVersion = null,
        string? installationRoot = null) =>
        ReadinessFixtures.Snapshot(
            BuildId,
            LiveObservation() with
            {
                SteamBuildId = steamBuildId ?? SteamBuildId,
                ExecutableVersion = executableVersion ?? ExecutableVersion,
                InstallationRoot = installationRoot ?? _gameDir
            },
            CapturedAt);

    private async Task SeedInstalledApiIndexesAsync(AuthorityHarness harness)
    {
        var environmentSnapshotId = EnvironmentSnapshotId.Create(TestSnapshot());
        await ReadinessFixtures.SeedCompletedCodeIndexAsync(
            harness.Repository,
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "s1api-installed",
            environmentSnapshotId,
            "index-s1api-installed",
            "snapshot-s1api-installed");
        await ReadinessFixtures.SeedCompletedCodeIndexAsync(
            harness.Repository,
            CodebaseKind.S1MApi,
            CodeChannel.Installed,
            "s1mapi-installed",
            environmentSnapshotId,
            "index-s1mapi-installed",
            "snapshot-s1mapi-installed");
    }

    private async Task SeedFullyReadyAsync(AuthorityHarness harness)
    {
        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync(BuildId);
        var indexId = "index-" + extractionId;
        await harness.SeedCompletedInstalledIndexAsync(extractionId, BuildId, indexId);
        await harness.Repository.SaveSnapshotAsync(TestSnapshot(), CancellationToken.None);
        await SeedInstalledApiIndexesAsync(harness);
        await ReadinessFixtures.SeedCompletedSceneSnapshotAsync(
            harness.Repository, harness.DatabasePath, BuildId, "scene-ready",
            extractionId, "snapshot-" + extractionId, indexId,
            TestContext.Current.CancellationToken);
        await ReadinessFixtures.SeedCompletedReferenceIndexAsync(
            harness.Repository, "qol", "index-reference-ready", "snapshot-reference-ready",
            BuildId, indexId, TestContext.Current.CancellationToken);
    }

    private AtlasReadinessService CreateService(
        AuthorityHarness harness,
        AtlasSchemaStatus? schema = null,
        DotNetRuntimeInfo? runtime = null,
        bool noGame = false,
        IReadOnlyList<ManagedToolStatus>? tools = null,
        IReadOnlyDictionary<CodebaseKind, IReadOnlyList<string>>? upstream = null) =>
        CreateServiceWithToolReader(
            harness,
            new StubToolReader(tools ??
            [
                ReadinessFixtures.VerifiedTool("cpp2il"),
                ReadinessFixtures.VerifiedTool("unity-classdata")
            ]),
            schema,
            runtime,
            noGame,
            upstream);

    private AtlasReadinessService CreateServiceWithToolReader(
        AuthorityHarness harness,
        IManagedToolStatusReader toolReader,
        AtlasSchemaStatus? schema = null,
        DotNetRuntimeInfo? runtime = null,
        bool noGame = false,
        IReadOnlyDictionary<CodebaseKind, IReadOnlyList<string>>? upstream = null) =>
        new(
            new StubSchemaInspector(schema ?? ReadinessFixtures.CurrentSchema),
            new StubRuntimeProbe(runtime ?? ReadinessFixtures.SupportedRuntime),
            new StubLocator(noGame ? null : GameInstall()),
            new StubMetadataReader(LiveObservation()),
            harness.Repository,
            harness.Repository,
            harness.CreatePreferredResolver(),
            harness.Repository,
            toolReader,
            new StubUpstreamCache(upstream ?? new Dictionary<CodebaseKind, IReadOnlyList<string>>()));
}
