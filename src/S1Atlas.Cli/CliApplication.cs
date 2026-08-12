using System.CommandLine;
using S1Atlas.Cli.Commands;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Cli;

public sealed class CliApplication
{
    private readonly AtlasPaths _paths;
    private readonly string _atlasVersion;

    public CliApplication(string dataDirectory, string atlasVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasVersion);

        _paths = new AtlasPaths(Path.GetFullPath(dataDirectory));
        _atlasVersion = atlasVersion;
    }

    public int Invoke(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return InvokeCore(args, output, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("S1Atlas operation was canceled.");
            return 2;
        }
        catch (Exception exception)
        {
            error.WriteLine($"S1Atlas failed: {exception.Message}");
            return 1;
        }
    }

    private int InvokeCore(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IAtlasRepository repository = new SqliteAtlasRepository(
            _paths.DatabasePath,
            _paths.BackupsDirectory);
        var discovery = new EnvironmentDiscoveryService(
            new WindowsScheduleOneLocator(),
            new Sha256FileHasher(),
            new InstalledDependencyDetector(),
            new WindowsInstallationMetadataReader());

        var root = new RootCommand(
            "Local Schedule I developer-intelligence tools.");
        root.Subcommands.Add(
            ScanCommand.Create(
                discovery,
                repository,
                _atlasVersion,
                output,
                error,
                cancellationToken));
        root.Subcommands.Add(
            StatusCommand.Create(repository, output, error, cancellationToken));
        root.Subcommands.Add(
            EnvironmentCommand.Create(repository, output, error, cancellationToken));
        root.Subcommands.Add(
            BuildsCommand.Create(repository, output, error, cancellationToken));

        return root.Parse(args).Invoke();
    }
}
