using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

public sealed class Phase5CleanupCliTests : IAsyncDisposable
{
    private const string AtlasVersion = "0.1.0-test";
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-13T12:00:00Z");
    private static readonly DateTimeOffset Old = Now - TimeSpan.FromDays(40);
    private static readonly DateTimeOffset Recent = Now - TimeSpan.FromDays(10);
    private static readonly string BuildId = new('a', 64);

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"s1atlas-phase5-cleanup-cli-{Guid.NewGuid():N}");
    private readonly string _dataDirectory;
    private readonly string _configurationDirectory;
    private int _requestCount;

    public Phase5CleanupCliTests()
    {
        _dataDirectory = Path.Combine(_temporaryDirectory, "data");
        _configurationDirectory = Path.Combine(_temporaryDirectory, "config");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_configurationDirectory);
    }

    [Fact]
    public async Task Cleanup_PreviewReportsEligibleAndBlockedButDeletesNothing()
    {
        await SeedAsync();
        var oldAttemptDir = AttemptDirectory(AttemptId(1));

        var preview = Invoke("extractions", "cleanup", "--json");

        Assert.Equal(0, preview.ExitCode);
        using var document = JsonDocument.Parse(preview.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("applied").GetBoolean());
        Assert.Empty(data.GetProperty("deletedItems").EnumerateArray());
        Assert.NotEmpty(data.GetProperty("eligibleItems").EnumerateArray());
        Assert.Contains(
            data.GetProperty("blockedItems").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "CleanupUnknownEntry");
        Assert.True(Directory.Exists(oldAttemptDir));
        Assert.Equal(0, _requestCount);
    }

    [Fact]
    public async Task Cleanup_ApplyDeletesOnlyEligibleAndIsIdempotent()
    {
        await SeedAsync();
        var oldAttemptDir = AttemptDirectory(AttemptId(1));
        var recentAttemptDir = AttemptDirectory(AttemptId(2));
        var oldQuarantine = Path.Combine(QuarantineDirectory(), OldQuarantineName());
        var unknownQuarantine = Path.Combine(QuarantineDirectory(), "not-owned");
        var validatedDir = Path.Combine(
            _dataDirectory, "builds", BuildId, "extractions", new string('c', 64));
        var snapshotDir = Path.Combine(
            _dataDirectory, "builds", BuildId, "inputs", new string('d', 64));

        var apply = Invoke("extractions", "cleanup", "--apply", "--json");

        // Blocked unknown quarantine remains, so apply exits 1 even though safe items deleted.
        Assert.Equal(1, apply.ExitCode);
        using (var document = JsonDocument.Parse(apply.StandardOutput))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("applied").GetBoolean());
            var deletedIds = data.GetProperty("deletedItems").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .ToArray();
            Assert.Contains(AttemptId(1), deletedIds);
            Assert.Contains(OldQuarantineName(), deletedIds);
        }

        // Eligible items are gone; everything protected survives.
        Assert.False(Directory.Exists(oldAttemptDir));
        Assert.False(Directory.Exists(oldQuarantine));
        Assert.True(Directory.Exists(recentAttemptDir));
        Assert.True(Directory.Exists(unknownQuarantine));
        Assert.True(Directory.Exists(validatedDir));
        Assert.True(Directory.Exists(snapshotDir));
        Assert.Null(await GetAttemptStatusAsync(AttemptId(1)));
        Assert.Equal("ProcessCompleted", await GetAttemptStatusAsync(AttemptId(3)));
        Assert.Equal(0, _requestCount);

        // A second apply deletes nothing new and remains idempotent.
        var second = Invoke("extractions", "cleanup", "--apply", "--json");
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        Assert.Empty(secondDocument.RootElement
            .GetProperty("data")
            .GetProperty("deletedItems")
            .EnumerateArray());
    }

    [Fact]
    public async Task Cleanup_InvalidDuration_FailsCleanlyWithoutStackTrace()
    {
        await SeedAsync();

        var result = Invoke("extractions", "cleanup", "--older-than", "5w", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("InvalidCleanupDuration", error.GetProperty("code").GetString());
        Assert.DoesNotContain("   at ", result.StandardOutput);
        Assert.DoesNotContain("   at ", result.StandardError);
    }

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

    private static string AttemptId(int index) => index.ToString("x32");

    private string AttemptDirectory(string attemptId) =>
        Path.Combine(_dataDirectory, "builds", BuildId, "attempts", attemptId);

    private string QuarantineDirectory() =>
        Path.Combine(_dataDirectory, "tools", "quarantine");

    private static string OldQuarantineName()
    {
        var stamp = Old.ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return $"cpp2il-1.0-{stamp}-{new string('0', 32)}";
    }

    private InvocationResult Invoke(params string[] arguments)
    {
        var application = new CliApplication(
            _dataDirectory,
            AtlasVersion,
            _configurationDirectory,
            CreateRejectingHttpClient,
            new FixedTimeProvider(Now),
            processExtractorFactory: null,
            isProcessAlive: _ => false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken);
        return new InvocationResult(exitCode, output.ToString(), error.ToString());
    }

    private async Task SeedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        await SeedBuildAsync(repository, cancellationToken);
        await CreateTerminalAttemptAsync(
            repository, AttemptId(1), ExtractionAttemptStatus.Failed, Old, cancellationToken);
        await CreateTerminalAttemptAsync(
            repository, AttemptId(2), ExtractionAttemptStatus.Failed, Recent, cancellationToken);
        await CreateProcessCompletedAttemptAsync(repository, AttemptId(3), cancellationToken);
        SqliteConnection.ClearAllPools();

        // Filesystem: attempt roots, an old + unknown quarantine, and protected final trees.
        CreateFileWithWrite(
            Path.Combine(AttemptDirectory(AttemptId(1)), "logs", "stdout.log"), Old);
        CreateFileWithWrite(
            Path.Combine(AttemptDirectory(AttemptId(2)), "logs", "stdout.log"), Recent);
        CreateFileWithWrite(
            Path.Combine(QuarantineDirectory(), OldQuarantineName(), "tool.bin"), Old);
        CreateFileWithWrite(
            Path.Combine(QuarantineDirectory(), "not-owned", "leftover.bin"), Old);
        CreateFileWithWrite(
            Path.Combine(
                _dataDirectory, "builds", BuildId, "extractions", new string('c', 64),
                "complete.marker"),
            Old);
        CreateFileWithWrite(
            Path.Combine(
                _dataDirectory, "builds", BuildId, "inputs", new string('d', 64),
                "game-root", "GameAssembly.dll"),
            Old);
    }

    private static async Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine("C:\\games", BuildId));
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(
                    BuildId,
                    "assembly-hash",
                    "metadata-hash",
                    Old,
                    IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3",
                    "3164500",
                    "123",
                    root,
                    Path.Combine(root, "GameAssembly.dll"),
                    Path.Combine(root, "global-metadata.dat")),
                Dependencies: [],
                AtlasVersion: "0.2.0-test",
                CapturedAtUtc: Old),
            cancellationToken);
    }

    private async Task CreateTerminalAttemptAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        ExtractionAttemptStatus status,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await repository.CreateAttemptAsync(CreateAttempt(attemptId), cancellationToken);
        await ExecuteAsync(
            """
            UPDATE extraction_attempts
            SET status = $status, completed_at_utc = $completedAtUtc
            WHERE attempt_id = $attemptId;
            """,
            cancellationToken,
            command =>
            {
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue(
                    "$completedAtUtc",
                    completedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$attemptId", attemptId);
            });
    }

    private async Task CreateProcessCompletedAttemptAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await repository.CreateAttemptAsync(CreateAttempt(attemptId), cancellationToken);
        await ExecuteAsync(
            """
            UPDATE extraction_attempts
            SET status = 'ProcessCompleted', candidate_output_path = $candidate
            WHERE attempt_id = $attemptId;
            """,
            cancellationToken,
            command =>
            {
                command.Parameters.AddWithValue(
                    "$candidate",
                    Path.Combine(AttemptDirectory(attemptId), "candidate-output"));
                command.Parameters.AddWithValue("$attemptId", attemptId);
            });
    }

    private async Task<string?> GetAttemptStatusAsync(string attemptId)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status FROM extraction_attempts WHERE attempt_id = $attemptId;";
        command.Parameters.AddWithValue("$attemptId", attemptId);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result as string;
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        Action<SqliteCommand> configure)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dataDirectory, "atlas.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    private static void CreateFileWithWrite(string path, DateTimeOffset lastWrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        // Backdate the owning directories so staging/quarantine age is genuinely old.
        for (var directory = Path.GetDirectoryName(path);
             directory is not null && directory.StartsWith(
                 Path.Combine(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
             directory = Path.GetDirectoryName(directory))
        {
            Directory.SetLastWriteTimeUtc(directory, lastWrite.UtcDateTime);
        }
    }

    private static ExtractionAttempt CreateAttempt(string attemptId) =>
        new(
            AttemptId: attemptId,
            RecipeId: "recipe-1",
            BuildId: BuildId,
            ToolInstanceId: null,
            ProfileId: "default",
            ProfileVersion: 1,
            ProfileDigest: new string('a', 64),
            ValidationPolicyId: "default",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: new string('b', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: null,
            InputSnapshotId: null,
            Status: ExtractionAttemptStatus.Created,
            CreatedAtUtc: Old,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: "C:\\attempts\\work",
            StandardOutputPath: "C:\\attempts\\logs\\stdout.log",
            StandardErrorPath: "C:\\attempts\\logs\\stderr.log",
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: null,
            ProcessExitCode: null,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            KeepFailedArtifacts: false,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: null,
            ResultExtractionId: null);

    private HttpClient CreateRejectingHttpClient() =>
        new(new RejectingHttpHandler(() => _requestCount++));

    private sealed class RejectingHttpHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
