using System.CommandLine;
using S1Atlas.Web;

namespace S1Atlas.Cli.Commands;

internal static class ServeCommand
{
    public static Command Create(
        string dataRoot,
        IBrowserLauncher launcher,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var portOption = new Option<int>("--port")
        {
            Description = "The loopback port to listen on. 0 picks an ephemeral port.",
            DefaultValueFactory = _ => ServeOptions.DefaultPort
        };
        var openOption = new Option<bool>("--open")
        {
            Description = "Open the default browser to the server URL once it is listening."
        };
        var command = new Command("serve", CliExamples.With("Start the local read-only web app on loopback.", "s1atlas serve --port 5217"));
        command.Options.Add(portOption);
        command.Options.Add(openOption);
        command.SetAction(parseResult => ServeRunner.RunAsync(
            dataRoot,
            parseResult.GetValue(portOption),
            parseResult.GetValue(openOption),
            launcher,
            output,
            error,
            cancellationToken));
        return command;
    }
}
