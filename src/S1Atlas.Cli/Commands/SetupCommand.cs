using System.CommandLine;
using S1Atlas.Application.Readiness;
using S1Atlas.Extraction;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

/// <summary>
/// An extract step's exit code plus the IDs a later scene step consumes: the
/// archived input snapshot to replay and the extraction the replay produced.
/// </summary>
internal sealed record SetupExtractResult(
    int ExitCode,
    string? ExtractionId = null,
    string? InputSnapshotId = null);

internal sealed record SetupStepRunners(
    Func<string?, CancellationToken, Task<int>> Scan,
    Func<string, CancellationToken, Task<int>> InstallTool,
    Func<ExtractionOptions, CancellationToken, Task<SetupExtractResult>> Extract,
    Func<string, CancellationToken, Task<int>> Promote,
    Func<IndexCommandOptions, CancellationToken, Task<int>> Index,
    Func<CancellationToken, Task<int>> IndexScene,
    Func<CancellationToken, Task<SceneInputState>> InspectSceneInput);

internal static class SetupCommand
{
    public static Command Create(
        IAtlasReadinessService readiness,
        SetupStepRunners runners,
        TextReader input,
        bool inputRedirected,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Run without asking for confirmation."
        };
        var sceneOption = new Option<bool>("--scene", "--include-optional")
        {
            Description =
                "Also build the scene snapshot, first archiving and replay-verifying the extraction " +
                "input it requires."
        };
        var command = new Command(
            "setup",
            CliExamples.With("Run the missing pipeline steps in order.", "s1atlas setup --yes", "s1atlas setup --scene"));
        command.Options.Add(yesOption);
        command.Options.Add(sceneOption);
        command.SetAction(parseResult =>
        {
            return Run(
                readiness,
                runners,
                input,
                inputRedirected,
                output,
                error,
                parseResult.GetValue(yesOption),
                parseResult.GetValue(sceneOption),
                cancellationToken);
        });
        return command;
    }

    private static int Run(
        IAtlasReadinessService readiness,
        SetupStepRunners runners,
        TextReader input,
        bool inputRedirected,
        TextWriter output,
        TextWriter error,
        bool assumeYes,
        bool includeScene,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var initial = readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult();
        var initialScan = FindItem(initial, ReadinessItemIds.Scan);
        if (initialScan is not null &&
            initialScan.State is ReadinessState.Stale or ReadinessState.Missing &&
            initialScan.FixCommand is null &&
            initialScan.ScanGamePath is null)
        {
            error.WriteLine("Setup cannot proceed automatically.");
            error.WriteLine(initialScan.Detail);
            return 1;
        }

        var plan = BuildPlan(initial, includeScene, runners, error, cancellationToken);
        if (plan.Count == 0)
        {
            if (initial.IsReady)
            {
                output.WriteLine("Already ready");
                return 0;
            }

            error.WriteLine("Setup cannot proceed automatically.");
            error.WriteLine(initial.NextStep.Summary);
            error.WriteLine("Run 's1atlas doctor' to re-check readiness.");
            return 1;
        }

        var scanSwitches = initialScan is not null && SwitchesInstalls(initialScan);
        var refreshScenePlanAfterScan = includeScene && initialScan is { State: not ReadinessState.Ok };
        output.WriteLine("Setup plan:");
        for (var index = 0; index < plan.Count; index++)
        {
            output.WriteLine(plan[index].OnlyIfRescanChangesBuild
                ? $"{index + 1}. {plan[index].Display} (if the scan records a new build)"
                : $"{index + 1}. {plan[index].Display}");
            if (scanSwitches &&
                string.Equals(plan[index].ItemId, ReadinessItemIds.Scan, StringComparison.Ordinal))
            {
                output.WriteLine(
                    $"   The recorded install at {initialScan!.ScanRecordedGamePath} no longer exists; " +
                    $"this would switch to {initialScan.ScanGamePath}.");
            }
        }

        if (refreshScenePlanAfterScan)
        {
            output.WriteLine(
                "After scanning, scene certification is planned for the selected build. " +
                "It may archive inputs, replay them, promote the replay, and rebuild the code index.");
        }

        if (scanSwitches && (assumeYes || inputRedirected))
        {
            error.WriteLine("Setup cannot switch game installs non-interactively.");
            error.WriteLine(initialScan!.Detail);
            if (initialScan.FixCommand is not null)
            {
                error.WriteLine($"Next: {initialScan.FixCommand}");
            }

            return 1;
        }

        if (!assumeYes)
        {
            if (inputRedirected)
            {
                WriteRedirectedError(error);
                return 1;
            }

            output.WriteLine($"Run these {plan.Count} steps? [y/N]");
            if (!IsAffirmative(input.ReadLine()))
            {
                output.WriteLine("Setup declined; no changes were made.");
                return 1;
            }
        }

        void RefreshScenePlan(ReadinessReport report)
        {
            initial = report;
            plan = BuildPlan(initial, includeScene, runners, error, cancellationToken);
            plan.RemoveAll(candidate => candidate.ItemId == ReadinessItemIds.Scan);
            output.WriteLine("Remaining setup plan after scan:");
            foreach (var remaining in plan)
            {
                output.WriteLine(remaining.Display);
            }

            refreshScenePlanAfterScan = false;
        }

        var networkConfirmed = assumeYes;
        for (var stepIndex = 0; stepIndex < plan.Count; stepIndex++)
        {
            var step = plan[stepIndex];
            var fresh = readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult();
            if (IsSatisfiedSincePlanning(step, initial, fresh))
            {
                output.WriteLine($"Skipping {step.Display}: already satisfied.");
                if (refreshScenePlanAfterScan && step.ItemId == ReadinessItemIds.Scan)
                {
                    RefreshScenePlan(fresh);
                    stepIndex = -1;
                }

                continue;
            }

            if (step.RequiresNetwork && !networkConfirmed)
            {
                if (inputRedirected)
                {
                    WriteRedirectedError(error);
                    return 1;
                }

                output.WriteLine("The next steps download tools from the network. Continue? [y/N]");
                if (!IsAffirmative(input.ReadLine()))
                {
                    output.WriteLine("Setup stopped before the network steps.");
                    return 1;
                }

                networkConfirmed = true;
            }

            var exitCode = step.Invoke(cancellationToken).GetAwaiter().GetResult();
            if (exitCode != 0)
            {
                error.WriteLine($"Step failed: {step.Display} (exit {exitCode}).");
                error.WriteLine(fresh.NextStep.Summary);
                error.WriteLine("Run 's1atlas doctor' to re-check readiness.");
                return 1;
            }

            if (refreshScenePlanAfterScan && step.ItemId == ReadinessItemIds.Scan)
            {
                // A conditional extraction can be skipped when a rescan keeps the
                // same build. Plan from the actual post-scan input instead of
                // assuming that extraction supplied an archived snapshot ID.
                RefreshScenePlan(readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult());
                stepIndex = -1;
            }
        }

        var final = readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult();
        if (final.IsReady)
        {
            output.WriteLine("Setup complete.");
            output.WriteLine(final.NextStep.Summary);
            return 0;
        }

        error.WriteLine("Setup finished but the atlas is not ready.");
        error.WriteLine(final.NextStep.Summary);
        error.WriteLine("Run 's1atlas doctor' to re-check readiness.");
        return 1;
    }

    private static List<SetupPlanStep> BuildPlan(
        ReadinessReport report,
        bool includeScene,
        SetupStepRunners runners,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ReadinessItem>(StringComparer.Ordinal);
        foreach (var item in report.Items)
        {
            items[item.Id] = item;
        }

        var plan = new List<SetupPlanStep>();
        var rescanPending = false;
        var extractionPlanned = false;
        var scene = new SceneInputProgress(runners);
        if (items.TryGetValue(ReadinessItemIds.Scan, out var scan) &&
            scan.State != ReadinessState.Ok)
        {
            var gamePath = scan.ScanGamePath;
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Scan,
                ScanDisplay(scan),
                false,
                null,
                cancellationToken => runners.Scan(gamePath, cancellationToken)));
            rescanPending = true;
        }

        if (items.TryGetValue(ReadinessItemIds.Tools, out var tools) &&
            tools.State is ReadinessState.Missing or ReadinessState.Stale)
        {
            foreach (var toolId in report.MissingRequiredToolIds)
            {
                var id = toolId;
                plan.Add(new SetupPlanStep(
                    ReadinessItemIds.Tools,
                    ReadinessFixCommands.InstallTool(id),
                    true,
                    id,
                    cancellationToken => runners.InstallTool(id, cancellationToken)));
            }
        }

        if (items.TryGetValue(ReadinessItemIds.Extraction, out var extraction) &&
            extraction.State != ReadinessState.Ok)
        {
            // The retry flag is matched against the canonical fix command, never
            // parsed out of the string: the same match decides both the displayed
            // command and the executed options, so the plan cannot promise --retry
            // while running the defaults.
            var retry = string.Equals(
                extraction.FixCommand, ReadinessFixCommands.ExtractRetry, StringComparison.Ordinal);
            var display = (retry, includeScene) switch
            {
                (true, true) => ReadinessFixCommands.ExtractSnapshotInputsRetry,
                (true, false) => ReadinessFixCommands.ExtractRetry,
                (false, true) => ReadinessFixCommands.ExtractSnapshotInputs,
                _ => ReadinessFixCommands.Extract
            };
            // With --scene the extraction also archives its inputs, so the snapshot
            // the preferred extraction points at needs only one certifying replay
            // later: no second extraction, promotion, or code index.
            var options = ExtractCommand.DefaultOptions with { Retry = retry, SnapshotInputs = includeScene };
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Extraction,
                display,
                false,
                null,
                scene.Extract(options)));
            extractionPlanned = true;
        }
        else if (rescanPending && extraction is not null)
        {
            // Extraction, index, and scene snapshot are all per-build. A stale scan
            // can record a new build, which turns these Ok items stale only after
            // the scan runs, so plan them now and skip them at run time if the
            // rescan kept the same build.
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Extraction,
                includeScene ? ReadinessFixCommands.ExtractSnapshotInputs : ReadinessFixCommands.Extract,
                false,
                null,
                scene.Extract(ExtractCommand.DefaultOptions with { SnapshotInputs = includeScene }),
                OnlyIfRescanChangesBuild: true));
            extractionPlanned = true;
        }

        if (items.TryGetValue(ReadinessItemIds.Index, out var index) &&
            index.State != ReadinessState.Ok)
        {
            // Same single-match rule as extraction: a mismatched index advertises
            // --force because only a forced rebuild skips the reuse path that
            // would return the mismatched completed index as success.
            var force = string.Equals(
                index.FixCommand, ReadinessFixCommands.IndexForce, StringComparison.Ordinal);
            var display = force ? ReadinessFixCommands.IndexForce : ReadinessFixCommands.Index;
            var options = new IndexCommandOptions(Force: force);
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Index,
                display,
                false,
                null,
                cancellationToken => runners.Index(options, cancellationToken)));
        }
        else if (rescanPending && index is not null)
        {
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Index,
                ReadinessFixCommands.Index,
                false,
                null,
                cancellationToken => runners.Index(new IndexCommandOptions(), cancellationToken),
                OnlyIfRescanChangesBuild: true));
        }

        if (plan.Count > 0 &&
            items.TryGetValue(ReadinessItemIds.Scan, out var satisfiedScan) &&
            satisfiedScan.State == ReadinessState.Ok)
        {
            plan.Insert(
                0,
                new SetupPlanStep(
                    ReadinessItemIds.Scan,
                    ScanDisplay(satisfiedScan),
                    false,
                    null,
                    cancellationToken => runners.Scan(satisfiedScan.ScanGamePath, cancellationToken)));
        }

        if (includeScene &&
            items.TryGetValue(ReadinessItemIds.Scene, out var sceneItem) &&
            (sceneItem.State != ReadinessState.Ok || rescanPending))
        {
            // An extraction this run makes decides the scene input, so only an
            // existing preferred extraction is inspected now.
            var input = extractionPlanned
                ? null
                : runners.InspectSceneInput(cancellationToken).GetAwaiter().GetResult();
            AddSceneSteps(
                plan, runners, scene, input, error, onlyIfRescanChangesBuild: sceneItem.State == ReadinessState.Ok);
        }

        return plan;
    }

    /// <summary>
    /// Plans the scene snapshot from the scene index gate's own verdict on the
    /// preferred extraction input (null when an earlier extraction step in this
    /// plan archives it). Every step is an existing command: the replay is the
    /// only thing that certifies a snapshot, and index --scene re-checks the gate.
    /// </summary>
    private static void AddSceneSteps(
        List<SetupPlanStep> plan,
        SetupStepRunners runners,
        SceneInputProgress scene,
        SceneInputState? input,
        TextWriter error,
        bool onlyIfRescanChangesBuild)
    {
        void Add(string display, Func<CancellationToken, Task<int>> invoke) =>
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Scene, display, false, null, invoke, onlyIfRescanChangesBuild));

        if (input?.Certification == SceneInputCertification.ReplayVerified)
        {
            Add(ReadinessFixCommands.IndexScene, runners.IndexScene);
            return;
        }

        // A replay is preference-blocked (same recipe, different output), so an
        // extraction that never archived its inputs is replaced by promoting the
        // replay of a fresh archive, and the code index is rebuilt for it.
        var promoteReplay = input is not null &&
            input.Certification != SceneInputCertification.NotReplayVerified;
        if (promoteReplay)
        {
            Add(ReadinessFixCommands.ExtractSnapshotInputsRetry, scene.Extract(
                ExtractCommand.DefaultOptions with { Retry = true, SnapshotInputs = true }));
        }
        else if (input is not null)
        {
            scene.InputSnapshotId = input.InputSnapshot!.InputSnapshotId;
        }

        Add(
            ReadinessFixCommands.ExtractFromInputSnapshot(scene.InputSnapshotId),
            async cancellationToken =>
            {
                if (scene.InputSnapshotId is not { } inputSnapshotId)
                {
                    error.WriteLine(
                        "No archived input snapshot is available to replay for the preferred extraction. " +
                        $"Run '{ReadinessFixCommands.SetupScene}' again to archive and certify one.");
                    return 1;
                }

                var result = await runners.Extract(
                    ExtractCommand.DefaultOptions with { Retry = true, InputSnapshotId = inputSnapshotId },
                    cancellationToken);
                scene.ExtractionId = result.ExtractionId;
                return result.ExitCode;
            });
        if (promoteReplay)
        {
            Add(
                ReadinessFixCommands.PromoteExtraction(null),
                cancellationToken => runners.Promote(scene.ExtractionId!, cancellationToken));
            Add(ReadinessFixCommands.Index, cancellationToken => runners.Index(new IndexCommandOptions(), cancellationToken));
        }

        Add(ReadinessFixCommands.IndexScene, runners.IndexScene);
    }

    private static string ScanDisplay(ReadinessItem scan) =>
        scan.FixCommand
        ?? (scan.ScanGamePath is null
            ? ReadinessFixCommands.Scan
            : ReadinessFixCommands.ScanAt(scan.ScanGamePath)
                ?? $"s1atlas scan --game-path {scan.ScanGamePath}");

    private static bool SwitchesInstalls(ReadinessItem scan) =>
        scan.ScanGamePath is not null &&
        scan.ScanRecordedGamePath is not null &&
        !string.Equals(
            Path.TrimEndingDirectorySeparator(scan.ScanGamePath),
            Path.TrimEndingDirectorySeparator(scan.ScanRecordedGamePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSatisfiedSincePlanning(
        SetupPlanStep step,
        ReadinessReport initial,
        ReadinessReport fresh)
    {
        if (step.ToolId is not null &&
            !fresh.MissingRequiredToolIds.Contains(step.ToolId, StringComparer.Ordinal))
        {
            return true;
        }

        return FindItem(fresh, step.ItemId)?.State == ReadinessState.Ok &&
            (step.OnlyIfRescanChangesBuild || FindItem(initial, step.ItemId)?.State != ReadinessState.Ok);
    }

    private static ReadinessItem? FindItem(ReadinessReport report, string id)
    {
        foreach (var item in report.Items)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsAffirmative(string? answer) =>
        answer is not null &&
        (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
            answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static void WriteRedirectedError(TextWriter error) =>
        error.WriteLine(
            "Cannot prompt for confirmation because input is redirected; " +
            "re-run with --yes to proceed non-interactively.");

    private sealed record SetupPlanStep(
        string ItemId,
        string Display,
        bool RequiresNetwork,
        string? ToolId,
        Func<CancellationToken, Task<int>> Invoke,
        bool OnlyIfRescanChangesBuild = false);

    /// <summary>
    /// The IDs one setup run hands from step to step on the way to a scene
    /// snapshot, which are known only once the step producing them has run.
    /// </summary>
    private sealed class SceneInputProgress(SetupStepRunners runners)
    {
        public string? InputSnapshotId { get; set; }

        public string? ExtractionId { get; set; }

        public Func<CancellationToken, Task<int>> Extract(ExtractionOptions options) =>
            async cancellationToken =>
            {
                var result = await runners.Extract(options, cancellationToken);
                InputSnapshotId = result.InputSnapshotId;
                return result.ExitCode;
            };
    }
}
