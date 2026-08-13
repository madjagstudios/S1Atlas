using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Cli.Commands;

internal static class ToolsStatusCommand
{
    public static Command Create(
        ManagedToolService service,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var toolIdArgument = new Argument<string?>("tool-id")
        {
            Description = "Optional stable tool ID to inspect.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(
            "status",
            "Inspect managed tool installations without network access.");
        command.Arguments.Add(toolIdArgument);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "tools status",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var statuses = service.GetStatusesAsync(
                            parseResult.GetValue(toolIdArgument),
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var data = new ToolsStatusOutput(
                        statuses.Select(ToOutput).ToArray());
                    return commandOutput.Success(
                        data,
                        writer => WriteHuman(writer, statuses));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    internal static ToolStatusOutput ToOutput(ManagedToolStatus status)
    {
        var installation = status.Installation;
        return new ToolStatusOutput(
            status.Definition.Definition.ToolId,
            status.Definition.Definition.DisplayName,
            status.Definition.Definition.Version,
            status.Definition.Definition.Platform,
            status.Definition.DefinitionDigest,
            status.Status.ToString(),
            installation is null
                ? null
                : ToolTrustLevel.ManagedPinned.ToString(),
            installation?.PackageSha256,
            installation?.ExecutableSha256,
            installation?.RootPath,
            installation?.InstalledAtUtc,
            installation?.LastVerifiedAtUtc,
            status.DiagnosticCode,
            status.DiagnosticMessage,
            installation?.ProbeResults
                .Select(probe => new ToolProbeOutput(
                    probe.ProbeId,
                    probe.Succeeded,
                    probe.ExitCode,
                    probe.TimedOut,
                    probe.FailureCode,
                    probe.FailureMessage))
                .ToArray() ?? []);
    }

    internal static ToolStatusOutput ToOutput(
        ManagedToolInstallation installation) =>
        new(
            installation.ToolId,
            installation.DisplayName,
            installation.Version,
            installation.Platform,
            installation.DefinitionDigest,
            installation.Status.ToString(),
            ToolTrustLevel.ManagedPinned.ToString(),
            installation.PackageSha256,
            installation.ExecutableSha256,
            installation.RootPath,
            installation.InstalledAtUtc,
            installation.LastVerifiedAtUtc,
            DiagnosticCode: null,
            DiagnosticMessage: null,
            installation.ProbeResults
                .Select(probe => new ToolProbeOutput(
                    probe.ProbeId,
                    probe.Succeeded,
                    probe.ExitCode,
                    probe.TimedOut,
                    probe.FailureCode,
                    probe.FailureMessage))
                .ToArray());

    private static void WriteHuman(
        TextWriter writer,
        IReadOnlyList<ManagedToolStatus> statuses)
    {
        for (var index = 0; index < statuses.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteLine();
            }

            WriteHuman(writer, statuses[index]);
        }
    }

    private static void WriteHuman(
        TextWriter writer,
        ManagedToolStatus status)
    {
        var definition = status.Definition;
        var installation = status.Installation;
        writer.WriteLine(definition.Definition.DisplayName);
        writer.WriteLine();
        writer.WriteLine($"Pinned version:       {definition.Definition.Version}");
        writer.WriteLine($"Platform:             {definition.Definition.Platform}");
        writer.WriteLine($"Definition digest:    {definition.DefinitionDigest}");
        writer.WriteLine($"Installation status:  {status.Status}");
        writer.WriteLine(
            $"Executable checksum:  {installation?.ExecutableSha256 ?? "unknown"}");
        writer.WriteLine(
            $"Install root:         {installation?.RootPath ?? "not installed"}");
        writer.WriteLine(
            $"Last verified:        " +
            (installation is null
                ? "never"
                : installation.LastVerifiedAtUtc.ToString("O")));

        if (status.Status == ToolInstallationStatus.NotInstalled)
        {
            writer.WriteLine();
            writer.WriteLine("Install with:");
            writer.WriteLine(
                $"  s1atlas tools install {definition.Definition.ToolId}");
        }
        else if (!string.IsNullOrWhiteSpace(status.DiagnosticMessage))
        {
            writer.WriteLine();
            writer.WriteLine(status.DiagnosticMessage);
        }
    }
}
