using System.CommandLine;
using S1Atlas.Cli.Commands;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Cli;

public sealed class CliApplication
{
    private readonly string _dataDirectory;
    private readonly string _atlasVersion;

    public CliApplication(string dataDirectory, string atlasVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasVersion);

        _dataDirectory = Path.GetFullPath(dataDirectory);
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

        IAtlasRepository repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"));
        var discovery = new EnvironmentDiscoveryService(
            new WindowsScheduleOneLocator(),
            new Sha256FileHasher(),
            new InstalledDependencyDetector());

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
            StatusCommand.Create(repository, output, cancellationToken));
        root.Subcommands.Add(
            EnvironmentCommand.Create(repository, output, error, cancellationToken));
        root.Subcommands.Add(
            BuildsCommand.Create(repository, output, cancellationToken));

        return root.Parse(args).Invoke();
    }
}
