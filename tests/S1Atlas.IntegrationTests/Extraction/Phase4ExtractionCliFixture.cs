using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Cli;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

/// <summary>
/// End-to-end Phase 4 CLI fixture. It composes the real <see cref="CliApplication"/> in
/// process over a temporary Atlas home and a test configuration whose validation policy
/// keeps only a tiny managed-byte floor (production <c>config/validation/*.json</c> is
/// untouched). Cpp2IL is never run: a <see cref="ScriptedProcessExtractor"/> supplies the
/// candidate bytes, a controlled <see cref="TimeProvider"/> fixes timestamps, and an HTTP
/// handler serves the source-built fake tool for the one offline managed install while
/// counting every request so tests can prove extraction and history stay offline.
/// </summary>
internal sealed class Phase4ExtractionCliFixture : IAsyncDisposable
{
    private const string AtlasVersion = "0.1.0-test";
    private const string ProfileId = "cpp2il-reconstructed-assemblies-v1";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"s1atlas-phase4-cli-tests-{Guid.NewGuid():N}");
    private readonly string _configurationDirectory;
    private readonly byte[] _packageBytes;
    private readonly TimeProvider _timeProvider =
        new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T07:00:00Z"));

    public Phase4ExtractionCliFixture(ScriptedProcessExtractor processExtractor)
    {
        DataDirectory = Path.Combine(_temporaryDirectory, "data");
        GameDirectory = Path.Combine(_temporaryDirectory, "Schedule I");
        _configurationDirectory = Path.Combine(_temporaryDirectory, "config");
        ProcessExtractor = processExtractor;
        // A self-contained native executable is used as the probe target for both the custom
        // and managed tool paths (the single-file managed installer only copies the one file,
        // so an apphost with a companion DLL would not survive the install). Cpp2IL is never
        // actually run for extraction; the scripted extractor supplies the candidate.
        CustomToolPath = ResolveCommandProcessorPath();
        _packageBytes = File.ReadAllBytes(CustomToolPath);
        Directory.CreateDirectory(_configurationDirectory);
        WriteConfiguration();
    }

    public string DataDirectory { get; }

    public string GameDirectory { get; }

    public string CustomToolPath { get; }

    public ScriptedProcessExtractor ProcessExtractor { get; }

    public int RequestCount { get; private set; }

    public string DatabasePath => Path.Combine(DataDirectory, "atlas.db");

    public InvocationResult Invoke(params string[] arguments)
    {
        var application = new CliApplication(
            DataDirectory,
            AtlasVersion,
            _configurationDirectory,
            CreateServingHttpClient,
            _timeProvider,
            () => ProcessExtractor,
            _ => false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken);
        return new InvocationResult(exitCode, output.ToString(), error.ToString());
    }

    public async Task<string> SeedBuildAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateGameAsync(GameDirectory, cancellationToken);
        var scan = Invoke("scan", "--game-path", GameDirectory);
        Assert.Equal(0, scan.ExitCode);

        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var snapshot = await repository.GetCurrentSnapshotAsync(cancellationToken);
        return Assert.IsType<string>(snapshot?.Build.BuildId);
    }

    /// <summary>
    /// Installs the managed Cpp2IL tool through the real <c>tools install</c> command using
    /// the served fake package, so a subsequent <c>extract</c> resolves ManagedPinned trust
    /// and can auto-prefer its output.
    /// </summary>
    public void InstallManagedTool()
    {
        var install = Invoke("tools", "install", "cpp2il");
        Assert.Equal(0, install.ExitCode);
    }

    public async Task<SqliteAtlasRepository> OpenRepositoryAsync()
    {
        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        return repository;
    }

    public string ValidatedExtractionRoot(string buildId, string extractionId) =>
        Path.Combine(DataDirectory, "builds", buildId, "extractions", extractionId);

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        if (Directory.Exists(_temporaryDirectory))
        {
            try
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task CreateGameAsync(string root, CancellationToken cancellationToken)
    {
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
        var metadataDirectory = Path.Combine(dataDirectory, "il2cpp_data", "Metadata");
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

    private void WriteConfiguration()
    {
        var extractionDirectory = Path.Combine(_configurationDirectory, "extraction");
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

        var validationDirectory = Path.Combine(_configurationDirectory, "validation");
        Directory.CreateDirectory(validationDirectory);
        // A test-only validation policy: identical to production except for the tiny managed
        // byte floor, so the small source-built Assembly-CSharp fixture validates. Production
        // config/validation/*.json is never edited by these tests.
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

        var toolsDirectory = Path.Combine(_configurationDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);
        var sha256 = Convert.ToHexString(SHA256.HashData(_packageBytes)).ToLowerInvariant();
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
                ExpectedSize = _packageBytes.LongLength,
                Sha256 = sha256,
                ExecutableRelativePath = "Cpp2IL.exe",
                Limits = new
                {
                    MaximumDownloadBytes = _packageBytes.LongLength,
                    MaximumExpandedBytes = _packageBytes.LongLength,
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
                    Arguments = new[] { "/d", "/c", "echo help" },
                    AcceptedExitCodes = new[] { 0 },
                    TimeoutSeconds = 10,
                    RequiredOutputSubstrings = new[] { "help" }
                },
                new
                {
                    ProbeId = "output-formats",
                    Arguments = new[] { "/d", "/c", "echo dll_il_recovery" },
                    AcceptedExitCodes = new[] { 0 },
                    TimeoutSeconds = 10,
                    RequiredOutputSubstrings = new[] { "dll_il_recovery" }
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

    private HttpClient CreateServingHttpClient() =>
        new(new ServingHttpHandler(this, _packageBytes))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static string ResolveCommandProcessorPath() =>
        Environment.GetEnvironmentVariable("ComSpec") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

    private sealed class ServingHttpHandler(Phase4ExtractionCliFixture owner, byte[] packageBytes)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(packageBytes)
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
