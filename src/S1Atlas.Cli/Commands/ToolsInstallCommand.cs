using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Cli.Commands;

internal static class ToolsInstallCommand
{
    public static Command Create(
        ManagedToolService service,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var toolIdArgument = new Argument<string>("tool-id")
        {
            Description = "Stable tool ID to install."
        };
        var repairOption = new Option<bool>("--repair")
        {
            Description = "Replace an invalid installation after quarantining it."
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(
            "install",
            "Download, verify, and register a repository-pinned tool.");
        command.Arguments.Add(toolIdArgument);
        command.Options.Add(repairOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "tools install",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var result = service.InstallAsync(
                            parseResult.GetRequiredValue(toolIdArgument),
                            parseResult.GetValue(repairOption),
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var data = new ToolInstallOutput(
                        ToolsStatusCommand.ToOutput(result.Installation),
                        result.WasAlreadyVerified,
                        result.Repaired,
                        result.QuarantinePath);
                    return commandOutput.Success(
                        data,
                        writer => WriteHuman(writer, result));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static void WriteHuman(TextWriter writer, ToolInstallResult result)
    {
        var installation = result.Installation;
        if (result.WasAlreadyVerified)
        {
            writer.WriteLine(
                $"{installation.DisplayName} {installation.Version} is already installed and verified.");
            writer.WriteLine("No work required.");
            return;
        }

        if (result.Repaired)
        {
            writer.WriteLine(
                $"{installation.DisplayName} {installation.Version} repaired and verified.");
            writer.WriteLine("Previous installation moved to:");
            writer.WriteLine($"  {result.QuarantinePath}");
            return;
        }

        writer.WriteLine(
            $"{installation.DisplayName} {installation.Version} installed and verified.");
    }
}
