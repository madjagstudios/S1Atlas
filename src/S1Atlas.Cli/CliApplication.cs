using System.CommandLine;
using S1Atlas.Cli.Commands;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Discovery;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Cli;

public sealed class CliApplication
{
    private readonly AtlasPaths _paths;
    private readonly string _atlasVersion;
    private readonly CliConfigurationPaths _configurationPaths;
    private readonly Func<HttpClient> _toolHttpClientFactory;
    private readonly TimeProvider _timeProvider;

    public CliApplication(string dataDirectory, string atlasVersion)
        : this(
            dataDirectory,
            atlasVersion,
            CliConfigurationPaths.Resolve().RootDirectory,
            () => CreateProductionToolHttpClient(atlasVersion),
            TimeProvider.System)
    {
    }

    internal CliApplication(
        string dataDirectory,
        string atlasVersion,
        string configurationDirectory,
        Func<HttpClient> toolHttpClientFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        ArgumentNullException.ThrowIfNull(toolHttpClientFactory);

        _paths = new AtlasPaths(Path.GetFullPath(dataDirectory));
        _atlasVersion = atlasVersion;
        _configurationPaths = new CliConfigurationPaths(
            Path.GetFullPath(configurationDirectory));
        _toolHttpClientFactory = toolHttpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var sqliteRepository = new SqliteAtlasRepository(
            _paths.DatabasePath,
            _paths.BackupsDirectory);
        IAtlasRepository repository = sqliteRepository;
        var discovery = new EnvironmentDiscoveryService(
            new WindowsScheduleOneLocator(),
            new Sha256FileHasher(),
            new InstalledDependencyDetector(),
            new WindowsInstallationMetadataReader());
        using var toolHttpClient = _toolHttpClientFactory() ??
            throw new InvalidOperationException(
                "The managed tool HTTP client factory returned null.");
        var fileHasher = new Sha256FileHasher();
        var definitionProvider = new RepositoryToolDefinitionProvider(
            _configurationPaths.ToolDefinitionsDirectory);
        var documentStore = new ToolInstallationDocumentStore();
        var probeRunner = new ToolProbeRunner();
        var validator = new ManagedToolInstallationValidator(
            _paths.ToolsDirectory,
            documentStore,
            probeRunner,
            fileHasher,
            _timeProvider);
        var installer = new ManagedToolInstaller(
            _paths.ToolsDirectory,
            _paths.ToolStagingDirectory,
            _paths.ToolQuarantineDirectory,
            validator,
            new ToolDownloadClient(toolHttpClient),
            new ToolPackageVerifier(fileHasher),
            new SafeToolPackageInstaller(),
            documentStore,
            probeRunner,
            fileHasher,
            _timeProvider);
        var toolService = new ManagedToolService(
            definitionProvider,
            validator,
            installer,
            sqliteRepository,
            ToolPlatform.GetCurrent(),
            _timeProvider);

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
        root.Subcommands.Add(
            ToolsCommand.Create(
                toolService,
                repository,
                output,
                error,
                cancellationToken));

        return root.Parse(args).Invoke();
    }

    private static HttpClient CreateProductionToolHttpClient(string atlasVersion)
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"S1Atlas/{atlasVersion}");
        return client;
    }
}
