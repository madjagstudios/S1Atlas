using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Deployment;

var paths = AtlasPaths.FromEnvironment();
var application = new CliApplication(paths.RootDirectory, AtlasVersion.For(typeof(CliApplication).Assembly));
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
