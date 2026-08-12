using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;

var paths = AtlasPaths.FromEnvironment();
var application = new CliApplication(paths.RootDirectory, "0.1.0");

return application.Invoke(
    args,
    Console.Out,
    Console.Error,
    CancellationToken.None);
