using System.CommandLine;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Commands;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction;
using S1Atlas.Indexing.Scene;
using Xunit;

namespace S1Atlas.IntegrationTests;

internal sealed class QueueReadinessService : IAtlasReadinessService
{
    private readonly Queue<ReadinessReport> _reports;
    private ReadinessReport _last;

    public QueueReadinessService(IEnumerable<ReadinessReport> reports)
    {
        _reports = new Queue<ReadinessReport>(reports);
        _last = _reports.Peek();
    }

    public int EvaluationCount { get; private set; }

    public Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken)
    {
        EvaluationCount++;
        if (_reports.Count > 0)
        {
            _last = _reports.Dequeue();
        }

        return Task.FromResult(_last);
    }
}

internal sealed class EvolvingReadinessService : IAtlasReadinessService
{
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);

    public int EvaluationCount { get; private set; }

    public void Complete(string step) => _completed.Add(step);

    public Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken)
    {
        EvaluationCount++;
        return Task.FromResult(SetupReportBuilder.FromCompleted(_completed));
    }
}

internal static class SetupReportBuilder
{
    public static ReadinessReport FullPipeline() =>
        new(
            [
                Ok(ReadinessItemIds.Scan, "Build scan", "Build scanned."),
                new ReadinessItem(
                    ReadinessItemIds.Tools, "Managed tools",
                    ReadinessState.Missing, "cpp2il is not installed.",
                    "s1atlas tools install cpp2il", false),
                NotApplicable(ReadinessItemIds.Extraction, "Preferred extraction", "Needs a scanned build first.", ReadinessFixCommands.Extract),
                NotApplicable(ReadinessItemIds.Index, "Schedule I index", "Needs a verified extraction first.", ReadinessFixCommands.Index),
                NotApplicable(ReadinessItemIds.Scene, "Scene snapshot (optional)", "Optional; needs a scanned build first.", ReadinessFixCommands.SetupScene, true)
            ],
            new ReadinessNextStep(false, "Next: s1atlas tools install cpp2il", "s1atlas tools install cpp2il"),
            false,
            ReadinessFixCommands.ExampleQuery,
            ["cpp2il"]);

    public static ReadinessReport ScanMissing() =>
        new(
            [
                new ReadinessItem(
                    ReadinessItemIds.Scan, "Build scan",
                    ReadinessState.Missing, "No environment snapshot has been scanned.",
                    ReadinessFixCommands.Scan, false),
                Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified."),
                NotApplicable(ReadinessItemIds.Extraction, "Preferred extraction", "Needs a scanned build first.", ReadinessFixCommands.Extract),
                NotApplicable(ReadinessItemIds.Index, "Schedule I index", "Needs a verified extraction first.", ReadinessFixCommands.Index)
            ],
            new ReadinessNextStep(false, "Next: s1atlas scan", ReadinessFixCommands.Scan),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);

    public static ReadinessReport RuntimeBlocked() =>
        new(
            [
                Ok(ReadinessItemIds.Scan, "Build scan", "Build scanned."),
                Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified."),
                Ok(ReadinessItemIds.Extraction, "Preferred extraction", "Extraction verified."),
                Ok(ReadinessItemIds.Index, "Schedule I index", "Index complete."),
                new ReadinessItem(
                    ReadinessItemIds.DotNetRuntime, ".NET runtime",
                    ReadinessState.Missing, "Install the .NET 8 runtime.", null, false)
            ],
            new ReadinessNextStep(false, "Next: Install the .NET 8 runtime.", null),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);

    public static ReadinessReport SceneMissing() =>
        new(
            [
                Ok(ReadinessItemIds.Scan, "Build scan", "Build scanned."),
                Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified."),
                Ok(ReadinessItemIds.Extraction, "Preferred extraction", "Extraction verified."),
                Ok(ReadinessItemIds.Index, "Schedule I index", "Index complete."),
                new ReadinessItem(
                    ReadinessItemIds.Scene, "Scene snapshot (optional)",
                    ReadinessState.Missing, "No scene snapshot is present.",
                    ReadinessFixCommands.SetupScene, true)
            ],
            new ReadinessNextStep(true, "Ready", ReadinessFixCommands.ExampleQuery),
            true,
            ReadinessFixCommands.ExampleQuery,
            []);

    public static ReadinessReport NoExtractionYet() =>
        new(
            [
                Ok(ReadinessItemIds.Scan, "Build scan", "Build scanned."),
                Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified."),
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "No preferred verified extraction exists for the build.",
                    ReadinessFixCommands.Extract, false),
                NotApplicable(ReadinessItemIds.Index, "Schedule I index", "Needs a verified extraction first.", ReadinessFixCommands.Index),
                new ReadinessItem(
                    ReadinessItemIds.Scene, "Scene snapshot (optional)",
                    ReadinessState.Missing, "No scene snapshot is present.",
                    ReadinessFixCommands.SetupScene, true)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract", ReadinessFixCommands.Extract),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);

    public static ReadinessReport GameUpdated() =>
        new(
            [
                new ReadinessItem(
                    ReadinessItemIds.Scan, "Build scan",
                    ReadinessState.Stale, "Steam build changed from 1 to 2.",
                    ReadinessFixCommands.Scan, false),
                Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified."),
                Ok(ReadinessItemIds.Extraction, "Preferred extraction", "Extraction verified."),
                Ok(ReadinessItemIds.Index, "Schedule I index", "Index complete.")
            ],
            new ReadinessNextStep(false, "Next: s1atlas scan", ReadinessFixCommands.Scan),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);

    public static ReadinessReport FromCompleted(IReadOnlySet<string> completed)
    {
        var scanOk = completed.Contains("scan");
        var toolsOk = completed.Contains("tools");
        var extractOk = completed.Contains("extract");
        var indexOk = completed.Contains("index");
        var ready = scanOk && toolsOk && extractOk && indexOk;
        return new ReadinessReport(
            [
                scanOk
                    ? Ok(ReadinessItemIds.Scan, "Build scan", "Build scanned.")
                    : new ReadinessItem(
                        ReadinessItemIds.Scan, "Build scan",
                        ReadinessState.Missing, "No environment snapshot has been scanned.",
                        ReadinessFixCommands.Scan, false),
                toolsOk
                    ? Ok(ReadinessItemIds.Tools, "Managed tools", "Tools verified.")
                    : new ReadinessItem(
                        ReadinessItemIds.Tools, "Managed tools",
                        ReadinessState.Missing, "cpp2il is not installed.",
                        "s1atlas tools install cpp2il", false),
                extractOk
                    ? Ok(ReadinessItemIds.Extraction, "Preferred extraction", "Extraction verified.")
                    : NotApplicable(ReadinessItemIds.Extraction, "Preferred extraction", "Needs a scanned build first.", ReadinessFixCommands.Extract),
                indexOk
                    ? Ok(ReadinessItemIds.Index, "Schedule I index", "Index complete.")
                    : NotApplicable(ReadinessItemIds.Index, "Schedule I index", "Needs a verified extraction first.", ReadinessFixCommands.Index)
            ],
            ready
                ? new ReadinessNextStep(true, "Ready", ReadinessFixCommands.ExampleQuery)
                : new ReadinessNextStep(false, "Next: s1atlas scan", ReadinessFixCommands.Scan),
            ready,
            ReadinessFixCommands.ExampleQuery,
            toolsOk ? [] : ["cpp2il"]);
    }

    private static ReadinessItem Ok(string id, string title, string detail) =>
        new(id, title, ReadinessState.Ok, detail, null, false);

    private static ReadinessItem NotApplicable(
        string id, string title, string detail, string? fix, bool optional = false) =>
        new(id, title, ReadinessState.NotApplicable, detail, fix, optional);
}

public sealed class SetupCommandTests
{
    [Fact]
    public void AlreadyReady_PrintsAlreadyReadyAndDoesNothing()
    {
        var readiness = new ScriptedReadinessService(ReadinessReportBuilder.Ready());
        var runners = ExplodingRunners();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("Already ready" + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(1, readiness.EvaluationCount);
    }

    [Fact]
    public void ConfirmYes_RunsPlanInOrderAndCompletes()
    {
        var readiness = new EvolvingReadinessService();
        var calls = new List<string>();
        var runners = RecordingRunners(readiness, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine + "y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan", "install-cpp2il", "extract", "index"], calls);
        Assert.Equal(1 + 4 + 1, readiness.EvaluationCount);
        var text = output.ToString();
        Assert.Contains("Setup plan:", text, StringComparison.Ordinal);
        Assert.Contains("1. s1atlas scan", text, StringComparison.Ordinal);
        Assert.Contains("2. s1atlas tools install cpp2il", text, StringComparison.Ordinal);
        Assert.Contains("3. s1atlas extract", text, StringComparison.Ordinal);
        Assert.Contains("4. s1atlas index", text, StringComparison.Ordinal);
        Assert.Contains("Setup complete.", text, StringComparison.Ordinal);
        Assert.Contains("Ready", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Decline_RunsNothing()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.ScanMissing());
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("n" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(1, exitCode);
        Assert.Empty(calls);
        Assert.Contains("Setup declined; no changes were made.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void YesFlag_SkipsBothPrompts()
    {
        var readiness = new EvolvingReadinessService();
        var calls = new List<string>();
        var runners = RecordingRunners(readiness, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan", "install-cpp2il", "extract", "index"], calls);
    }

    [Fact]
    public void RedirectedInput_FailsFastWithoutHanging()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.ScanMissing());
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: true, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(1, exitCode);
        Assert.Empty(calls);
        Assert.Contains("input is redirected", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--yes", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, readiness.EvaluationCount);
    }

    [Fact]
    public void ToolsDecline_StopsBeforeNetworkSteps()
    {
        var readiness = new EvolvingReadinessService();
        var calls = new List<string>();
        var runners = RecordingRunners(readiness, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine + "n" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(["scan"], calls);
        Assert.Contains("Setup stopped before the network steps.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StopAtFirstFailure_PrintsFailureAndNextStep()
    {
        var readiness = new QueueReadinessService(
        [
            SetupReportBuilder.FullPipeline(),
            SetupReportBuilder.FullPipeline(),
            SetupReportBuilder.FullPipeline()
        ]);
        var calls = new List<string>();
        var runners = new SetupStepRunners(
            Scan: (_, _) =>
            {
                calls.Add("scan");
                return Task.FromResult(0);
            },
            InstallTool: (_, _) =>
            {
                calls.Add("install");
                return Task.FromResult(1);
            },
            Extract: (_, _) =>
            {
                calls.Add("extract");
                return Task.FromResult(new SetupExtractResult(0));
            },
            Index: (_, _) =>
            {
                calls.Add("index");
                return Task.FromResult(0);
            },
            IndexScene: _ =>
            {
                calls.Add("scene");
                return Task.FromResult(0);
            },
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine + "y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(["scan", "install"], calls);
        Assert.Contains("Step failed: s1atlas tools install cpp2il (exit 1).", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Next:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recheck_SkipsNewlySatisfiedStep()
    {
        var readiness = new QueueReadinessService(
        [
            SetupReportBuilder.ScanMissing(),
            SetupReportBuilder.FromCompleted(new HashSet<string>(["scan"], StringComparer.Ordinal)),
            SetupReportBuilder.FromCompleted(new HashSet<string>(["scan"], StringComparer.Ordinal)),
            SetupReportBuilder.FromCompleted(new HashSet<string>(["scan"], StringComparer.Ordinal)),
            ReadinessReportBuilder.Ready()
        ]);
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["extract", "index"], calls);
        Assert.Contains("Skipping s1atlas scan: already satisfied.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GameUpdate_OneRunRescansExtractsAndIndexes()
    {
        var readiness = new QueueReadinessService(
        [
            SetupReportBuilder.GameUpdated(),
            SetupReportBuilder.GameUpdated(),
            SetupReportBuilder.FromCompleted(new HashSet<string>(["scan", "tools"], StringComparer.Ordinal)),
            SetupReportBuilder.FromCompleted(new HashSet<string>(["scan", "tools", "extract"], StringComparer.Ordinal)),
            ReadinessReportBuilder.Ready()
        ]);
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan", "extract", "index"], calls);
        var text = output.ToString();
        Assert.Contains("1. s1atlas scan", text, StringComparison.Ordinal);
        Assert.Contains("2. s1atlas extract (if the scan records a new build)", text, StringComparison.Ordinal);
        Assert.Contains("3. s1atlas index (if the scan records a new build)", text, StringComparison.Ordinal);
        Assert.Contains("Setup complete.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SameBuildRescan_SkipsExtractAndIndex()
    {
        var sameBuild = SetupReportBuilder.FromCompleted(
            new HashSet<string>(["scan", "tools", "extract", "index"], StringComparer.Ordinal));
        var readiness = new QueueReadinessService(
            [SetupReportBuilder.GameUpdated(), SetupReportBuilder.GameUpdated(), sameBuild]);
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan"], calls);
        Assert.Contains("Skipping s1atlas extract: already satisfied.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Skipping s1atlas index: already satisfied.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeOptionalAlias_ReplayVerifiedInput_PlansOnlyTheSceneIndex()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.SceneMissing());
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls, SceneInput(SceneInputCertification.ReplayVerified));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--include-optional"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scene"], calls);
        Assert.Contains("1. s1atlas index --scene", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SceneInputCertification.NoInputSnapshot, 2, false)]
    [InlineData(SceneInputCertification.NotReplayVerified, 1, false)]
    [InlineData(SceneInputCertification.ReplayVerified, 0, false)]
    [InlineData(SceneInputCertification.NoInputSnapshot, 2, true)]
    [InlineData(SceneInputCertification.NotReplayVerified, 1, true)]
    [InlineData(SceneInputCertification.ReplayVerified, 0, true)]
    public void Scene_SameBuildRescan_UsesTheExistingExtractionInput(
        SceneInputCertification certification, int expectedExtractions, bool scanAlreadySatisfied)
    {
        var sameBuild = SetupReportBuilder.SceneMissing();
        var stale = sameBuild with
        {
            Items = sameBuild.Items.Select(item => item.Id == ReadinessItemIds.Scan
                ? item with { State = ReadinessState.Stale, FixCommand = ReadinessFixCommands.Scan }
                : item).ToArray()
        };
        var readiness = new QueueReadinessService([stale, scanAlreadySatisfied ? sameBuild : stale, sameBuild]);
        var calls = new List<string>();
        var extractions = new List<ExtractionOptions>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, SceneRunners(calls, extractions, SceneInput(certification)),
            new StringReader(string.Empty), inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--scene", "--yes"]);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(expectedExtractions, extractions.Count);
        Assert.Equal(!scanAlreadySatisfied, calls.Contains("scan"));
        Assert.Equal("scene", calls[^1]);
        Assert.Equal(certification == SceneInputCertification.NoInputSnapshot,
            calls.Contains("promote:" + ReplayExtractionId));
    }

    [Fact]
    public void Scene_ExtractionWithoutArchive_ArchivesReplaysPromotesAndReindexes()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.SceneMissing());
        var calls = new List<string>();
        var extractions = new List<ExtractionOptions>();
        var runners = SceneRunners(calls, extractions, SceneInput(SceneInputCertification.NoInputSnapshot));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--scene", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["extract", "extract", "promote:" + ReplayExtractionId, "index", "scene"], calls);
        Assert.True(extractions[0] is { SnapshotInputs: true, Retry: true, InputSnapshotId: null });
        Assert.True(extractions[1] is { SnapshotInputs: false, Retry: true, InputSnapshotId: ArchivedSnapshotId });
        var text = output.ToString();
        Assert.Contains("1. s1atlas extract --snapshot-inputs --retry", text, StringComparison.Ordinal);
        Assert.Contains("2. s1atlas extract --input-snapshot <snapshot-id> --retry", text, StringComparison.Ordinal);
        Assert.Contains("3. s1atlas extractions promote <extraction-id>", text, StringComparison.Ordinal);
        Assert.Contains("4. s1atlas index", text, StringComparison.Ordinal);
        Assert.Contains("5. s1atlas index --scene", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_NewBuildRescan_ArchivesAndReplaysTheNewExtraction()
    {
        var sameBuild = SetupReportBuilder.SceneMissing();
        var stale = sameBuild with
        {
            Items = sameBuild.Items.Select(item => item.Id == ReadinessItemIds.Scan
                ? item with { State = ReadinessState.Stale, FixCommand = ReadinessFixCommands.Scan }
                : item).ToArray()
        };
        var fresh = SetupReportBuilder.NoExtractionYet();
        var readiness = new QueueReadinessService(
            [stale, stale, fresh, fresh, fresh, fresh, fresh, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var extractions = new List<ExtractionOptions>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, SceneRunners(calls, extractions, sceneInput: null),
            new StringReader(string.Empty), inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--scene", "--yes"]);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(["scan", "extract", "index", "extract", "scene"], calls);
        Assert.True(extractions[0] is { SnapshotInputs: true, InputSnapshotId: null });
        Assert.Equal(ArchivedSnapshotId, extractions[1].InputSnapshotId);
    }

    [Fact]
    public void Scene_FreshExtraction_ArchivesItsInputsSoOneReplayCertifiesThem()
    {
        var fresh = SetupReportBuilder.NoExtractionYet();
        var readiness = new QueueReadinessService(
            [fresh, fresh, fresh, fresh, fresh, fresh, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var extractions = new List<ExtractionOptions>();
        var runners = SceneRunners(calls, extractions, sceneInput: null);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--scene", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan", "extract", "index", "extract", "scene"], calls);
        Assert.True(extractions[0] is { SnapshotInputs: true, Retry: false });
        Assert.True(extractions[1] is { Retry: true, InputSnapshotId: ArchivedSnapshotId });
        var text = output.ToString();
        Assert.Contains("2. s1atlas extract --snapshot-inputs", text, StringComparison.Ordinal);
        Assert.Contains("4. s1atlas extract --input-snapshot <snapshot-id> --retry", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_UnverifiedArchive_ReplaysItThenIndexesTheScene()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.SceneMissing());
        var calls = new List<string>();
        var extractions = new List<ExtractionOptions>();
        var runners = SceneRunners(calls, extractions, SceneInput(SceneInputCertification.NotReplayVerified));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--scene", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["extract", "scene"], calls);
        Assert.True(Assert.Single(extractions) is { Retry: true, InputSnapshotId: ArchivedSnapshotId });
        Assert.Contains(
            $"1. s1atlas extract --input-snapshot {ArchivedSnapshotId} --retry",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutIncludeOptional_SceneStepIsNotPlanned()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.SceneMissing());
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, ExplodingRunners(), new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("Already ready" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void CannotProceed_WhenPlanIsEmptyButNotReady()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.RuntimeBlocked());
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(1, exitCode);
        Assert.Empty(calls);
        Assert.Contains("Setup cannot proceed automatically.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Next: Install the .NET 8 runtime.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingToolIds_DrivePerToolInstallsInOrder()
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Tools, "Managed tools",
                    ReadinessState.Missing, "cpp2il is not installed; unity-classdata is not installed.",
                    "s1atlas tools install cpp2il", false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas tools install cpp2il", "s1atlas tools install cpp2il"),
            false,
            ReadinessFixCommands.ExampleQuery,
            ["cpp2il", "unity-classdata"]);
        var readiness = new ScriptedReadinessService(report);
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine + "y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(["install-cpp2il", "install-unity-classdata"], calls);
        Assert.Contains("1. s1atlas tools install cpp2il", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2. s1atlas tools install unity-classdata", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForceIndex_PlanShowsForceAndRunnerReceivesForce()
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Index, "Schedule I index",
                    ReadinessState.Missing, "The completed index does not match the preferred extraction.",
                    ReadinessFixCommands.IndexForce, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas index --force", ReadinessFixCommands.IndexForce),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new ScriptedReadinessService(report);
        IndexCommandOptions? received = null;
        var runners = new SetupStepRunners(
            Scan: (_, _) => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (_, _) => throw new InvalidOperationException("Extract must not run."),
            Index: (options, _) =>
            {
                received = options;
                return Task.FromResult(0);
            },
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."),
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("1. s1atlas index --force", output.ToString(), StringComparison.Ordinal);
        Assert.True(Assert.IsType<IndexCommandOptions>(received).Force);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetryExtract_PlanShowsRetryAndFailureStopsBeforeIndexing(bool includeScene)
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "The preferred extraction failed integrity verification.",
                    ReadinessFixCommands.ExtractRetry, false),
                new ReadinessItem(
                    ReadinessItemIds.Index, "Schedule I index",
                    ReadinessState.Missing, "Needs a verified extraction first.",
                    ReadinessFixCommands.Index, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract --retry", ReadinessFixCommands.ExtractRetry),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new ScriptedReadinessService(report);
        ExtractionOptions? received = null;
        var runners = new SetupStepRunners(
            Scan: (_, _) => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (options, _) =>
            {
                received = options;
                return Task.FromResult(new SetupExtractResult(1));
            },
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."),
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, includeScene ? ["setup", "--scene", "--yes"] : ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        var expectedCommand = includeScene
            ? ReadinessFixCommands.ExtractSnapshotInputsRetry
            : ReadinessFixCommands.ExtractRetry;
        Assert.Contains($"1. {expectedCommand}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2. s1atlas index", output.ToString(), StringComparison.Ordinal);
        var options = Assert.IsType<ExtractionOptions>(received);
        Assert.True(options.Retry);
        Assert.Equal(includeScene, options.SnapshotInputs);
    }

    [Fact]
    public void MissingFixCommand_RunsDefaultExtractAndShowsDefault()
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "No preferred extraction exists.",
                    null, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract", ReadinessFixCommands.Extract),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new ScriptedReadinessService(report);
        ExtractionOptions? received = null;
        var runners = new SetupStepRunners(
            Scan: (_, _) => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (options, _) =>
            {
                received = options;
                return Task.FromResult(new SetupExtractResult(0));
            },
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."),
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("1. s1atlas extract", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--retry", output.ToString(), StringComparison.Ordinal);
        Assert.False(Assert.IsType<ExtractionOptions>(received).Retry);
    }

    private static SetupStepRunners ExplodingRunners() =>
        new(
            Scan: (_, _) => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (_, _) => throw new InvalidOperationException("Extract must not run."),
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."),
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));

    private const string ArchivedSnapshotId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ReplayExtractionId = "2222222222222222222222222222222222222222222222222222222222222222";

    private static SceneInputState SceneInput(SceneInputCertification certification) =>
        new(
            certification,
            certification is SceneInputCertification.NotReplayVerified or SceneInputCertification.ReplayVerified
                ? new InputSnapshot(
                    ArchivedSnapshotId, new string('a', 64), "inputs", new string('d', 64), DateTimeOffset.UnixEpoch,
                    certification == SceneInputCertification.ReplayVerified, null, new InputManifest([]))
                : null);

    // An archiving extraction reports ArchivedSnapshotId; a replay reports ReplayExtractionId.
    private static SetupStepRunners SceneRunners(
        List<string> calls,
        List<ExtractionOptions> extractions,
        SceneInputState? sceneInput) =>
        new(
            Scan: (_, _) =>
            {
                calls.Add("scan");
                return Task.FromResult(0);
            },
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (options, _) =>
            {
                calls.Add("extract");
                extractions.Add(options);
                return Task.FromResult(options.InputSnapshotId is null
                    ? new SetupExtractResult(0, new string('3', 64), options.SnapshotInputs ? ArchivedSnapshotId : null)
                    : new SetupExtractResult(0, ReplayExtractionId, options.InputSnapshotId));
            },
            Index: (_, _) =>
            {
                calls.Add("index");
                return Task.FromResult(0);
            },
            IndexScene: _ =>
            {
                calls.Add("scene");
                return Task.FromResult(0);
            },
            Promote: (extractionId, _) =>
            {
                calls.Add("promote:" + extractionId);
                return Task.FromResult(0);
            },
            InspectSceneInput: _ => Task.FromResult(
                sceneInput ?? throw new InvalidOperationException("Scene input must not be inspected.")));

    private static SetupStepRunners RecordingRunners(
        EvolvingReadinessService? readiness,
        List<string> calls,
        SceneInputState? sceneInput = null) =>
        new(
            Scan: (_, _) =>
            {
                calls.Add("scan");
                readiness?.Complete("scan");
                return Task.FromResult(0);
            },
            InstallTool: (toolId, _) =>
            {
                calls.Add("install-" + toolId);
                readiness?.Complete("tools");
                return Task.FromResult(0);
            },
            Extract: (_, _) =>
            {
                calls.Add("extract");
                readiness?.Complete("extract");
                return Task.FromResult(new SetupExtractResult(0));
            },
            Index: (_, _) =>
            {
                calls.Add("index");
                readiness?.Complete("index");
                return Task.FromResult(0);
            },
            IndexScene: _ =>
            {
                calls.Add("scene");
                return Task.FromResult(0);
            },
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => Task.FromResult(
                sceneInput ?? throw new InvalidOperationException("Scene input must not be inspected.")));

    [Fact]
    public void StaleRecordedScan_PassesRecordedPathToRunnerAndShowsItInPlan()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale, "Steam build changed from 1 to 2.",
            $"s1atlas scan --game-path \"{recorded}\"", false, recorded);
        var stale = ScanOnlyReport(scan);
        var readiness = new QueueReadinessService([stale, stale, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan:" + recorded], calls);
        Assert.Contains(
            $"1. s1atlas scan --game-path \"{recorded}\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SatisfiedScan_RefreshStepUsesRecordedPath()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Scan, "Build scan",
                    ReadinessState.Ok, "Build scanned.", null, false, recorded),
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "No preferred verified extraction exists for the build.",
                    ReadinessFixCommands.Extract, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract", ReadinessFixCommands.Extract),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new QueueReadinessService([report, report, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan:" + recorded, "extract"], calls);
        Assert.Contains(
            $"1. s1atlas scan --game-path \"{recorded}\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchInteractive_ShowsBothPathsAndScansDiscovered()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        const string discovered = "C:\\Steam\\Schedule I";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale,
            $"The scanned install at {recorded} no longer exists; Schedule I was found at {discovered}.",
            $"s1atlas scan --game-path \"{discovered}\"", false, discovered, recorded);
        var stale = ScanOnlyReport(scan);
        var readiness = new QueueReadinessService([stale, stale, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan:" + discovered], calls);
        Assert.Contains(recorded, output.ToString(), StringComparison.Ordinal);
        Assert.Contains(discovered, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchNonInteractiveRedirected_RefusesWithoutScanning()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        const string discovered = "C:\\Steam\\Schedule I";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale,
            $"The scanned install at {recorded} no longer exists; Schedule I was found at {discovered}.",
            $"s1atlas scan --game-path \"{discovered}\"", false, discovered, recorded);
        var readiness = new ScriptedReadinessService(ScanOnlyReport(scan));
        var runners = ExplodingRunners();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: true, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(1, exitCode);
        Assert.Contains(recorded, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(discovered, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("s1atlas scan --game-path", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchNonInteractiveYes_RefusesWithoutScanning()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        const string discovered = "C:\\Steam\\Schedule I";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale,
            $"The scanned install at {recorded} no longer exists; Schedule I was found at {discovered}.",
            $"s1atlas scan --game-path \"{discovered}\"", false, discovered, recorded);
        var readiness = new ScriptedReadinessService(ScanOnlyReport(scan));
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Empty(calls);
        Assert.Contains(recorded, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("s1atlas scan --game-path", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GoneWithoutDiscovery_RunsNoScanAndShowsGuidance()
    {
        const string recorded = "C:\\Modding\\S1 Copy (x86)";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale,
            $"The scanned install at {recorded} no longer exists. " +
            "Run 's1atlas scan --game-path <folder>' with the folder that holds Schedule I.",
            null, false);
        var readiness = new ScriptedReadinessService(ScanOnlyReport(scan));
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Empty(calls);
        Assert.Contains(recorded, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--game-path <folder>", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnquotableScanPath_ShowsRawFolderInPlan()
    {
        const string raw = "C:\\we\"ird";
        var scan = new ReadinessItem(
            ReadinessItemIds.Scan, "Build scan",
            ReadinessState.Stale, "Steam build changed from 1 to 2.",
            null, false, raw);
        var stale = ScanOnlyReport(scan);
        var readiness = new QueueReadinessService([stale, stale, ReadinessReportBuilder.Ready()]);
        var calls = new List<string>();
        var runners = PathCapturingRunners(calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scan:" + raw], calls);
        Assert.Contains("1. s1atlas scan --game-path " + raw, output.ToString(), StringComparison.Ordinal);
    }

    private static ReadinessReport ScanOnlyReport(ReadinessItem scan) =>
        new(
            [scan],
            new ReadinessNextStep(false, $"Next: {scan.FixCommand ?? scan.Detail}", scan.FixCommand),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);

    private static SetupStepRunners PathCapturingRunners(List<string> calls) =>
        new(
            Scan: (gamePath, _) =>
            {
                calls.Add("scan:" + (gamePath ?? "<null>"));
                return Task.FromResult(0);
            },
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (_, _) =>
            {
                calls.Add("extract");
                return Task.FromResult(new SetupExtractResult(0));
            },
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."),
            Promote: (_, _) => throw new InvalidOperationException("Promote must not run."),
            InspectSceneInput: _ => throw new InvalidOperationException("Scene input must not be inspected."));

    private static int InvokeSetup(Command command, string[] args)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(command);
        return root.Parse(args).Invoke();
    }
}
