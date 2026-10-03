using System.CommandLine;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Commands;
using S1Atlas.Extraction;
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
                NotApplicable(ReadinessItemIds.Scene, "Scene snapshot (optional)", "Optional; needs a scanned build first.", ReadinessFixCommands.IndexScene, true)
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
                    ReadinessFixCommands.IndexScene, true)
            ],
            new ReadinessNextStep(true, "Ready", ReadinessFixCommands.ExampleQuery),
            true,
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
            Scan: _ =>
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
                return Task.FromResult(0);
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
            });
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
    public void IncludeOptional_AddsSceneStep()
    {
        var readiness = new ScriptedReadinessService(SetupReportBuilder.SceneMissing());
        var calls = new List<string>();
        var runners = RecordingRunners(null, calls);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader("y" + Environment.NewLine),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--include-optional"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["scene"], calls);
        Assert.Contains("s1atlas index --scene", output.ToString(), StringComparison.Ordinal);
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
            Scan: _ => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (_, _) => throw new InvalidOperationException("Extract must not run."),
            Index: (options, _) =>
            {
                received = options;
                return Task.FromResult(0);
            },
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."));
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

    [Fact]
    public void RetryExtract_PlanShowsRetryAndRunnerReceivesRetry()
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "The preferred extraction failed integrity verification.",
                    ReadinessFixCommands.ExtractRetry, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract --retry", ReadinessFixCommands.ExtractRetry),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new ScriptedReadinessService(report);
        ExtractionOptions? received = null;
        var runners = new SetupStepRunners(
            Scan: _ => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (options, _) =>
            {
                received = options;
                return Task.FromResult(0);
            },
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = SetupCommand.Create(
            readiness, runners, new StringReader(string.Empty),
            inputRedirected: false, output, error,
            TestContext.Current.CancellationToken);

        var exitCode = InvokeSetup(command, ["setup", "--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("1. s1atlas extract --retry", output.ToString(), StringComparison.Ordinal);
        Assert.True(Assert.IsType<ExtractionOptions>(received).Retry);
    }

    [Fact]
    public void MissingFixCommand_RunsDefaultExtractAndShowsDefault()
    {
        var report = new ReadinessReport(
            [
                new ReadinessItem(
                    ReadinessItemIds.Extraction, "Preferred extraction",
                    ReadinessState.Missing, "The preferred extraction failed integrity verification.",
                    null, false)
            ],
            new ReadinessNextStep(false, "Next: s1atlas extract", ReadinessFixCommands.Extract),
            false,
            ReadinessFixCommands.ExampleQuery,
            []);
        var readiness = new ScriptedReadinessService(report);
        ExtractionOptions? received = null;
        var runners = new SetupStepRunners(
            Scan: _ => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (options, _) =>
            {
                received = options;
                return Task.FromResult(0);
            },
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."));
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
            Scan: _ => throw new InvalidOperationException("Scan must not run."),
            InstallTool: (_, _) => throw new InvalidOperationException("Install must not run."),
            Extract: (_, _) => throw new InvalidOperationException("Extract must not run."),
            Index: (_, _) => throw new InvalidOperationException("Index must not run."),
            IndexScene: _ => throw new InvalidOperationException("Scene index must not run."));

    private static SetupStepRunners RecordingRunners(
        EvolvingReadinessService? readiness,
        List<string> calls) =>
        new(
            Scan: _ =>
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
                return Task.FromResult(0);
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
            });

    private static int InvokeSetup(Command command, string[] args)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(command);
        return root.Parse(args).Invoke();
    }
}
