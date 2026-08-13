using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Extraction;
using S1Atlas.ManagedAssemblyFixture;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

internal sealed class ExtractionCliFixture : IAsyncDisposable
{
    private const string AtlasVersion = "0.1.0-test";
    private const string ProfileId = "cpp2il-reconstructed-assemblies-v1";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"s1atlas-extraction-cli-tests-{Guid.NewGuid():N}");
    private readonly string _configurationDirectory;

    public ExtractionCliFixture(ScriptedProcessExtractor? processExtractor = null)
    {
        DataDirectory = Path.Combine(_temporaryDirectory, "data");
        GameDirectory = Path.Combine(_temporaryDirectory, "Schedule I");
        AlternateGameDirectory = Path.Combine(_temporaryDirectory, "alternate", "Schedule I");
        _configurationDirectory = Path.Combine(_temporaryDirectory, "config");
        ProcessExtractor = processExtractor;
        FakeCpp2IlPath = FindFakeCpp2Il();
        Directory.CreateDirectory(_configurationDirectory);
        WriteConfiguration();
    }

    public string DataDirectory { get; }

    public string GameDirectory { get; }

    public string AlternateGameDirectory { get; }

    public string FakeCpp2IlPath { get; }

    public ScriptedProcessExtractor? ProcessExtractor { get; }

    public int RequestCount { get; private set; }

    public string DatabasePath => Path.Combine(DataDirectory, "atlas.db");

    public Task CreateGameAsync(string? directory = null) =>
        CreateGameCoreAsync(directory, TestContext.Current.CancellationToken);

    private async Task CreateGameCoreAsync(
        string? directory,
        CancellationToken cancellationToken)
    {
        var root = directory ?? GameDirectory;
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "GameAssembly.dll"),
            "deterministic fake game assembly"u8.ToArray(),
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "Schedule I.exe"),
            "deterministic fake executable support"u8.ToArray(),
            cancellationToken);

        var dataDirectory = Path.Combine(root, "Schedule I_Data");
        var metadataDirectory = Path.Combine(
            dataDirectory,
            "il2cpp_data",
            "Metadata");
        Directory.CreateDirectory(metadataDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(metadataDirectory, "global-metadata.dat"),
            "deterministic fake metadata"u8.ToArray(),
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dataDirectory, "globalgamemanagers"),
            "deterministic fake unity version source"u8.ToArray(),
            cancellationToken);
    }

    public InvocationResult Invoke(params string[] arguments) =>
        InvokeWithCancellation(TestContext.Current.CancellationToken, arguments);

    public InvocationResult InvokeWithCancellation(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var application = new CliApplication(
            DataDirectory,
            AtlasVersion,
            _configurationDirectory,
            CreateRejectingHttpClient,
            TimeProvider.System,
            ProcessExtractor is null ? null : () => ProcessExtractor,
            _ => false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            arguments,
            output,
            error,
            cancellationToken);
        return new InvocationResult(
            exitCode,
            output.ToString(),
            error.ToString());
    }

    public Task<string> SeedBuildAsync(string? directory = null) =>
        SeedBuildCoreAsync(directory, TestContext.Current.CancellationToken);

    private async Task<string> SeedBuildCoreAsync(
        string? directory,
        CancellationToken cancellationToken)
    {
        var root = directory ?? GameDirectory;
        await CreateGameCoreAsync(root, cancellationToken);
        var result = InvokeWithCancellation(
            cancellationToken,
            "scan",
            "--game-path",
            root);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var snapshot = await repository.GetCurrentSnapshotAsync(cancellationToken);
        return Assert.IsType<string>(snapshot?.Build.BuildId);
    }

    public Task<(string GameAssembly, string Metadata)> GetAuthoritativeHashesAsync(
        string? directory = null) =>
        GetAuthoritativeHashesCoreAsync(
            directory,
            TestContext.Current.CancellationToken);

    private async Task<(string GameAssembly, string Metadata)> GetAuthoritativeHashesCoreAsync(
        string? directory,
        CancellationToken cancellationToken)
    {
        var root = directory ?? GameDirectory;
        var assembly = await HashFileAsync(
            Path.Combine(root, "GameAssembly.dll"),
            cancellationToken);
        var metadata = await HashFileAsync(
            Path.Combine(
                root,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat"),
            cancellationToken);
        return (assembly, metadata);
    }

    public void RequireProbeFailure()
    {
        WriteToolDefinition(requiredOutputSubstring: "output-that-is-never-emitted");
    }

    public ValueTask DisposeAsync()
    {
        Assert.Equal(0, RequestCount);
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private void WriteConfiguration()
    {
        WriteToolDefinition(requiredOutputSubstring: "dll_il_recovery");

        var extractionDirectory = Path.Combine(
            _configurationDirectory,
            "extraction");
        Directory.CreateDirectory(extractionDirectory);
        File.WriteAllText(
            Path.Combine(extractionDirectory, $"{ProfileId}.json"),
            """
            {
              "schemaVersion": 1,
              "profileId": "cpp2il-reconstructed-assemblies-v1",
              "profileVersion": 1,
              "adapterVersion": 1,
              "extractionSchemaVersion": 1,
              "executableName": "Schedule I",
              "outputFormat": "dll_il_recovery",
              "timeoutSeconds": 1800,
              "maximumRetainedStandardOutputBytes": 1048576,
              "maximumRetainedStandardErrorBytes": 1048576,
              "acceptedExitCodes": [0],
              "requiredAssemblyIdentities": ["Assembly-CSharp"],
              "snapshotInputs": [
                { "relativePath": "GameAssembly.dll", "role": "gameAssembly" },
                { "relativePath": "Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat", "role": "globalMetadata" },
                { "relativePath": "Schedule I.exe", "role": "executableSupport" }
              ],
              "unityVersionSources": ["Schedule I_Data/globalgamemanagers"]
            }
            """);

        var validationDirectory = Path.Combine(
            _configurationDirectory,
            "validation");
        Directory.CreateDirectory(validationDirectory);
        File.WriteAllText(
            Path.Combine(validationDirectory, "managed-assemblies-v1.json"),
            """
            {
              "schemaVersion": 1,
              "policyId": "managed-assemblies-v1",
              "policyVersion": 1,
              "requiredAssemblyIdentities": ["Assembly-CSharp"],
              "minimumManagedAssemblyCount": 1,
              "minimumTypeDefinitionCount": 1,
              "minimumMethodDefinitionCount": 1,
              "minimumTotalManagedBytes": 1,
              "comparativeWarningRelativeChange": 0.25,
              "catastrophicDecreaseRelativeChange": 0.80
            }
            """);
    }

    private void WriteToolDefinition(string requiredOutputSubstring)
    {
        var toolsDirectory = Path.Combine(_configurationDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);
        var executableBytes = File.ReadAllBytes(FakeCpp2IlPath);
        var sha256 = Convert
            .ToHexString(SHA256.HashData(executableBytes))
            .ToLowerInvariant();
        var document = new
        {
            SchemaVersion = 1,
            ToolId = "cpp2il",
            DisplayName = "Cpp2IL",
            Version = "source-built-fake",
            Platform = "win-x64",
            Package = new
            {
                Kind = "singleFile",
                ArchiveFormat = (string?)null,
                SourceUrl = "https://example.test/Cpp2IL.exe",
                ReleaseUrl = "https://example.test/releases/fake",
                AssetName = "Cpp2IL.exe",
                ExpectedSize = executableBytes.LongLength,
                Sha256 = sha256,
                ExecutableRelativePath = "Cpp2IL.exe",
                Limits = new
                {
                    MaximumDownloadBytes = executableBytes.LongLength,
                    MaximumExpandedBytes = executableBytes.LongLength,
                    MaximumFileCount = 1
                }
            },
            License = new
            {
                SpdxIdentifier = "MIT",
                SourceUrl = "https://example.test/LICENSE"
            },
            Probes = new object[]
            {
                new
                {
                    ProbeId = "help",
                    Arguments = new[] { "--help" },
                    AcceptedExitCodes = new[] { 0 },
                    TimeoutSeconds = 10,
                    RequiredOutputSubstrings = Array.Empty<string>()
                },
                new
                {
                    ProbeId = "output-formats",
                    Arguments = new[] { "--list-output-formats" },
                    AcceptedExitCodes = new[] { 0 },
                    TimeoutSeconds = 10,
                    RequiredOutputSubstrings = new[] { requiredOutputSubstring }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(toolsDirectory, "cpp2il.win-x64.json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }

    private HttpClient CreateRejectingHttpClient() =>
        new(new RejectingHttpHandler(this))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static string FindFakeCpp2Il()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException(
                $"Could not locate S1Atlas.sln above '{AppContext.BaseDirectory}'.");
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executable = Path.Combine(
            current.FullName,
            "tests",
            "S1Atlas.FakeCpp2Il",
            "bin",
            configuration,
            "net8.0",
            "S1Atlas.FakeCpp2Il.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The source-built fake Cpp2IL apphost is missing.",
                executable);
        }

        return executable;
    }

    private sealed class RejectingHttpHandler(ExtractionCliFixture owner)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            owner.RequestCount++;
            throw new InvalidOperationException(
                $"Extraction attempted HTTP request '{request.RequestUri}'.");
        }
    }
}

internal sealed class ScriptedProcessExtractor : IIl2CppExtractor
{
    private readonly ScriptedProcessOutcome _outcome;

    public ScriptedProcessExtractor(ScriptedProcessOutcome outcome)
    {
        _outcome = outcome;
    }

    public TaskCompletionSource Started { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ObservedCancellationToken { get; private set; }

    public string? ObservedGameRoot { get; private set; }

    public async Task<ExtractionProcessResult> ExtractAsync(
        ExtractionProcessRequest request,
        Func<int, CancellationToken, Task> processStarted,
        CancellationToken cancellationToken)
    {
        ObservedCancellationToken = cancellationToken;
        ObservedGameRoot = request.GameRoot;
        var startedAt = DateTimeOffset.UtcNow;
        await processStarted(4242, cancellationToken);
        Started.TrySetResult();

        if (_outcome == ScriptedProcessOutcome.Canceled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        Directory.CreateDirectory(request.OutputDirectory);
        if (_outcome == ScriptedProcessOutcome.ValidManagedAssembly)
        {
            File.Copy(
                typeof(FixtureRoot).Assembly.Location,
                Path.Combine(request.OutputDirectory, "Assembly-CSharp.dll"),
                overwrite: true);
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(request.OutputDirectory, "partial-or-candidate.dll"),
                "source-built fake extraction output",
                CancellationToken.None);
        }

        await File.WriteAllTextAsync(
            request.StandardOutputPath,
            "fake stdout",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            request.StandardErrorPath,
            "fake stderr",
            CancellationToken.None);

        if (_outcome == ScriptedProcessOutcome.MutateInput)
        {
            await File.AppendAllTextAsync(
                Path.Combine(request.GameRoot, "GameAssembly.dll"),
                "mutated",
                CancellationToken.None);
        }

        var completedAt = DateTimeOffset.UtcNow;
        return new ExtractionProcessResult(
            _outcome == ScriptedProcessOutcome.TimedOut
                ? ExtractionProcessTerminationReason.TimedOut
                : ExtractionProcessTerminationReason.Exited,
            ProcessId: 4242,
            ExitCode: _outcome == ScriptedProcessOutcome.NonZero ? 7 :
                _outcome == ScriptedProcessOutcome.TimedOut ? null : 0,
            startedAt,
            completedAt,
            new ExtractionLogResult(
                request.StandardOutputPath,
                RetainedBytes: 11,
                DiscardedBytes: 0,
                Truncated: false),
            new ExtractionLogResult(
                request.StandardErrorPath,
                RetainedBytes: 11,
                DiscardedBytes: 0,
                Truncated: false),
            StartFailureMessage: null);
    }
}

internal enum ScriptedProcessOutcome
{
    NonZero,
    TimedOut,
    Canceled,
    MutateInput,
    ValidManagedAssembly,

    // The process exits 0 without mutating input, but the candidate is not a valid managed
    // Assembly-CSharp, so validation (not the process) rejects it as non-authoritative.
    InvalidManagedAssembly
}

internal sealed record InvocationResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
