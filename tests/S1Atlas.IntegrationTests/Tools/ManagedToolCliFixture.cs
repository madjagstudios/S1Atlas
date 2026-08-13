using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using Xunit;

namespace S1Atlas.IntegrationTests.Tools;

internal sealed class ManagedToolCliFixture : IAsyncDisposable
{
    private const string AtlasVersion = "0.1.0-test";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"s1atlas-managed-tool-cli-tests-{Guid.NewGuid():N}");
    private readonly string _configurationDirectory;
    private readonly byte[] _packageBytes;
    private readonly DateTimeOffset _now =
        DateTimeOffset.Parse("2026-08-13T06:00:00Z");

    public ManagedToolCliFixture()
    {
        DataDirectory = Path.Combine(_temporaryDirectory, "data");
        _configurationDirectory = Path.Combine(_temporaryDirectory, "config");
        _packageBytes = File.ReadAllBytes(GetCommandProcessorPath());
        PackageSha256 = Convert
            .ToHexString(SHA256.HashData(_packageBytes))
            .ToLowerInvariant();
        Directory.CreateDirectory(_configurationDirectory);
        WriteDefinition();
    }

    public string DataDirectory { get; }

    public string PackageSha256 { get; }

    public int RequestCount { get; private set; }

    public string InstallRoot => Path.Combine(
        new AtlasPaths(DataDirectory).ToolsDirectory,
        "cpp2il",
        "test-version");

    public string ExecutablePath => Path.Combine(InstallRoot, "Cpp2IL.exe");

    public string QuarantineRoot =>
        new AtlasPaths(DataDirectory).ToolQuarantineDirectory;

    public InvocationResult Invoke(params string[] arguments)
    {
        var application = new CliApplication(
            DataDirectory,
            AtlasVersion,
            _configurationDirectory,
            CreateHttpClient,
            new FixedTimeProvider(_now));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken);
        return new InvocationResult(
            exitCode,
            output.ToString(),
            error.ToString());
    }

    public void WriteDefinition(string? sha256 = null)
    {
        var toolsDirectory = Path.Combine(_configurationDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);
        var document = new
        {
            SchemaVersion = 1,
            ToolId = "cpp2il",
            DisplayName = "Cpp2IL",
            Version = "test-version",
            Platform = "win-x64",
            Package = new
            {
                Kind = "singleFile",
                ArchiveFormat = (string?)null,
                SourceUrl = "https://example.test/tool.exe",
                ReleaseUrl = "https://example.test/releases/tool",
                AssetName = "tool.exe",
                ExpectedSize = _packageBytes.LongLength,
                Sha256 = sha256 ?? PackageSha256,
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
                    Arguments = new[]
                    {
                        "/d", "/c", "echo dll_il_recovery"
                    },
                    AcceptedExitCodes = new[] { 0 },
                    TimeoutSeconds = 10,
                    RequiredOutputSubstrings = new[] { "dll_il_recovery" }
                }
            }
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(
            Path.Combine(toolsDirectory, "cpp2il.win-x64.json"),
            json);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private HttpClient CreateHttpClient() =>
        new(new FakeToolHandler(this, _packageBytes))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static string GetCommandProcessorPath() =>
        Environment.GetEnvironmentVariable("ComSpec") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

    private sealed class FakeToolHandler(
        ManagedToolCliFixture owner,
        byte[] packageBytes) : HttpMessageHandler
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

internal sealed record InvocationResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
