using System.CommandLine;
using S1Atlas.Application.Readiness;
using S1Atlas.Extraction;

namespace S1Atlas.Cli.Commands;

internal sealed record SetupStepRunners(
    Func<string?, CancellationToken, Task<int>> Scan,
    Func<string, CancellationToken, Task<int>> InstallTool,
    Func<ExtractionOptions, CancellationToken, Task<int>> Extract,
    Func<IndexCommandOptions, CancellationToken, Task<int>> Index,
    Func<CancellationToken, Task<int>> IndexScene);

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
        var includeOptionalOption = new Option<bool>("--include-optional")
        {
            Description = "Include optional steps such as the scene snapshot."
        };
        var command = new Command("setup", CliExamples.With("Run the missing pipeline steps in order.", "s1atlas setup --yes"));
        command.Options.Add(yesOption);
        command.Options.Add(includeOptionalOption);
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
                parseResult.GetValue(includeOptionalOption),
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
        bool includeOptional,
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

        var plan = BuildPlan(initial, includeOptional, runners);
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
        output.WriteLine("Setup plan:");
        for (var index = 0; index < plan.Count; index++)
        {
            output.WriteLine($"{index + 1}. {plan[index].Display}");
            if (scanSwitches &&
                string.Equals(plan[index].ItemId, ReadinessItemIds.Scan, StringComparison.Ordinal))
            {
                output.WriteLine(
                    $"   The recorded install at {initialScan!.ScanRecordedGamePath} no longer exists; " +
                    $"this would switch to {initialScan.ScanGamePath}.");
            }
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

        var networkConfirmed = assumeYes;
        foreach (var step in plan)
        {
            var fresh = readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult();
            if (IsSatisfiedSincePlanning(step, initial, fresh))
            {
                output.WriteLine($"Skipping {step.Display}: already satisfied.");
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
        bool includeOptional,
        SetupStepRunners runners)
    {
        var items = new Dictionary<string, ReadinessItem>(StringComparer.Ordinal);
        foreach (var item in report.Items)
        {
            items[item.Id] = item;
        }

        var plan = new List<SetupPlanStep>();
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
            var display = retry ? ReadinessFixCommands.ExtractRetry : ReadinessFixCommands.Extract;
            var options = retry
                ? ExtractCommand.DefaultOptions with { Retry = true }
                : ExtractCommand.DefaultOptions;
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Extraction,
                display,
                false,
                null,
                cancellationToken => runners.Extract(options, cancellationToken)));
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

        if (includeOptional &&
            items.TryGetValue(ReadinessItemIds.Scene, out var scene) &&
            scene.State != ReadinessState.Ok)
        {
            plan.Add(new SetupPlanStep(
                ReadinessItemIds.Scene,
                scene.FixCommand ?? ReadinessFixCommands.IndexScene,
                false,
                null,
                runners.IndexScene));
        }

        return plan;
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
            FindItem(initial, step.ItemId)?.State != ReadinessState.Ok;
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
        Func<CancellationToken, Task<int>> Invoke);
}
