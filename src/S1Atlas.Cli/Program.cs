using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;

var paths = AtlasPaths.FromEnvironment();
var application = new CliApplication(paths.RootDirectory, "0.1.0");
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    return application.Invoke(
        args,
        Console.Out,
        Console.Error,
        cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
