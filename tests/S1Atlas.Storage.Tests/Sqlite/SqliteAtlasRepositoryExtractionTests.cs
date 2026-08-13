using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryExtractionTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryExtractionTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-extraction-repository-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task CreateAttemptAsync_CreatedAttemptIsInsertedOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var attempt = CreateAttempt("00000000000000000000000000000001");

        await repository.CreateAttemptAsync(attempt, cancellationToken);

        Assert.Equal(attempt, await repository.GetAttemptAsync(
            attempt.AttemptId,
            cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateAttemptAsync(attempt, cancellationToken));
        Assert.Equal(1L, await CountAsync("extraction_attempts", cancellationToken));
    }

    [Fact]
    public async Task TransitionAttemptAsync_UsesExpectedStatusForOptimisticConcurrency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var created = CreateAttempt("00000000000000000000000000000002");
        await repository.CreateAttemptAsync(created, cancellationToken);
        var preparing = created with
        {
            Status = ExtractionAttemptStatus.Preparing,
            StartedAtUtc = DateTimeOffset.Parse("2026-08-13T03:01:00Z")
        };

        await repository.TransitionAttemptAsync(
            preparing,
            expectedStatus: ExtractionAttemptStatus.Created,
            cancellationToken);
        var running = preparing with
        {
            Status = ExtractionAttemptStatus.Running,
            ProcessId = 31415
        };
        await repository.TransitionAttemptAsync(
            running,
            expectedStatus: ExtractionAttemptStatus.Preparing,
            cancellationToken);
        var stale = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = "C:\\attempts\\candidate"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TransitionAttemptAsync(
                stale,
                expectedStatus: ExtractionAttemptStatus.Created,
                cancellationToken));

        Assert.Equal(running, await repository.GetAttemptAsync(
            created.AttemptId,
            cancellationToken));
    }

    [Fact]
    public async Task TransitionAttemptAsync_ProcessCompletedAndFailureTerminalsAreImmutable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);

        var processCompleted = CreateAttempt("00000000000000000000000000000003");
        await repository.CreateAttemptAsync(processCompleted, cancellationToken);
        processCompleted = processCompleted with
        {
            Status = ExtractionAttemptStatus.Preparing,
            StartedAtUtc = DateTimeOffset.Parse("2026-08-13T03:01:00Z")
        };
        await repository.TransitionAttemptAsync(
            processCompleted,
            ExtractionAttemptStatus.Created,
            cancellationToken);
        processCompleted = processCompleted with
        {
            Status = ExtractionAttemptStatus.Running,
            ProcessId = 100
        };
        await repository.TransitionAttemptAsync(
            processCompleted,
            ExtractionAttemptStatus.Preparing,
            cancellationToken);
        processCompleted = processCompleted with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = "C:\\attempts\\candidate"
        };
        await repository.TransitionAttemptAsync(
            processCompleted,
            ExtractionAttemptStatus.Running,
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TransitionAttemptAsync(
                processCompleted with { Status = ExtractionAttemptStatus.Validating },
                ExtractionAttemptStatus.ProcessCompleted,
                cancellationToken));

        var terminalCases = new[]
        {
            ("00000000000000000000000000000004", ExtractionAttemptStatus.Failed),
            ("00000000000000000000000000000005", ExtractionAttemptStatus.Canceled),
            ("00000000000000000000000000000006", ExtractionAttemptStatus.Abandoned)
        };
        foreach (var (attemptId, terminalStatus) in terminalCases)
        {
            var created = CreateAttempt(attemptId);
            await repository.CreateAttemptAsync(created, cancellationToken);
            var terminal = terminalStatus == ExtractionAttemptStatus.Failed
                ? created with
                {
                    Status = terminalStatus,
                    CompletedAtUtc = DateTimeOffset.Parse("2026-08-13T03:02:00Z"),
                    FailureStage = ExtractionFailureStage.ProcessExecution,
                    FailureCode = ExtractionFailureCode.ProcessExitNonZero,
                    FailureMessage = "Cpp2IL exited with code 1."
                }
                : created with
                {
                    Status = terminalStatus,
                    CompletedAtUtc = DateTimeOffset.Parse("2026-08-13T03:02:00Z")
                };
            await repository.TransitionAttemptAsync(
                terminal,
                ExtractionAttemptStatus.Created,
                cancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.TransitionAttemptAsync(
                    terminal with
                    {
                        Status = ExtractionAttemptStatus.Preparing,
                        FailureStage = null,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    terminalStatus,
                    cancellationToken));
            Assert.Equal(terminal, await repository.GetAttemptAsync(
                attemptId,
                cancellationToken));
        }
    }

    [Fact]
    public async Task ListNonTerminalAttemptsAsync_ExcludesEveryTerminalState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var statuses = Enum.GetValues<ExtractionAttemptStatus>();
        for (var index = 0; index < statuses.Length; index++)
        {
            var attempt = CreateAttempt((index + 16).ToString("x32"));
            await repository.CreateAttemptAsync(attempt, cancellationToken);
            await SetStatusAsync(attempt.AttemptId, statuses[index], cancellationToken);
        }

        var attempts = await repository.ListNonTerminalAttemptsAsync(
            cancellationToken);

        Assert.Equal(
            [
                ExtractionAttemptStatus.Created,
                ExtractionAttemptStatus.Preparing,
                ExtractionAttemptStatus.Running,
                ExtractionAttemptStatus.Validating
            ],
            attempts.Select(attempt => attempt.Status).Order());
    }

    [Fact]
    public async Task AttemptPersistence_RoundTripsEveryFieldExactly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await SeedToolInstanceAsync(cancellationToken);
        var snapshot = CreateInputSnapshot(
            "snapshot-roundtrip",
            "build-a",
            replayVerified: true);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var attempt = CreateAttempt("00000000000000000000000000000007") with
        {
            RecipeId = "recipe-1",
            ToolInstanceId = "tool-instance-1",
            ProfileId = "strict-profile",
            ProfileVersion = 3,
            ProfileDigest = new string('1', 64),
            ValidationPolicyId = "policy-1",
            ValidationPolicyVersion = 4,
            ValidationPolicyDigest = new string('2', 64),
            AdapterVersion = 5,
            ExtractionSchemaVersion = 6,
            InputSource = ExtractionInputSource.ArchivedSnapshot,
            InputSnapshotId = snapshot.InputSnapshotId,
            CreatedAtUtc = DateTimeOffset.Parse("2026-08-13T03:00:00.1234567+00:00"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-13T03:01:00.1234567+00:00"),
            CompletedAtUtc = DateTimeOffset.Parse("2026-08-13T03:02:00.1234567+00:00"),
            PreInputManifestDigest = new string('3', 64),
            PostInputManifestDigest = new string('4', 64),
            WorkingPath = "C:\\attempts\\work",
            StandardOutputPath = "C:\\attempts\\stdout.log",
            StandardErrorPath = "C:\\attempts\\stderr.log",
            StandardOutputTruncated = true,
            StandardErrorTruncated = true,
            StandardOutputDiscardedBytes = 101,
            StandardErrorDiscardedBytes = 202,
            ProcessId = 303,
            ProcessExitCode = 17,
            KeepFailedArtifacts = true,
            DiscardedFileCount = 8,
            DiscardedByteCount = 404,
            CandidateOutputPath = "C:\\attempts\\candidate"
        };

        await repository.CreateAttemptAsync(attempt, cancellationToken);

        Assert.Equal(attempt, await repository.GetAttemptAsync(
            attempt.AttemptId,
            cancellationToken));
        Assert.Null(await repository.GetAttemptAsync(
            "ffffffffffffffffffffffffffffffff",
            cancellationToken));
    }

    [Fact]
    public async Task GetBuildAndInstallationObservationQueries_ReturnCurrentAndHistoricalFactsInOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "historical-build", cancellationToken);
        await SeedBuildAsync(repository, "current-build", cancellationToken);
        await InsertObservationAsync(
            "observation-a",
            "historical-build",
            "2026-08-13T01:00:00.0000000+00:00",
            cancellationToken);
        await InsertObservationAsync(
            "observation-b",
            "historical-build",
            "2026-08-13T02:00:00.0000000+00:00",
            cancellationToken);
        await InsertObservationAsync(
            "observation-c",
            "historical-build",
            "2026-08-13T02:00:00.0000000+00:00",
            cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var historical = await repository.GetBuildAsync(
            "historical-build",
            cancellationToken);
        var observations = await repository.ListInstallationObservationsAsync(
            "historical-build",
            cancellationToken);

        Assert.Equal("current-build", current!.Build.BuildId);
        Assert.Equal("historical-build", historical!.BuildId);
        Assert.Null(await repository.GetBuildAsync("missing-build", cancellationToken));
        Assert.Equal(
            ["observation-c", "observation-b", "observation-a"],
            observations.Take(3).Select(observation => observation.SnapshotId));
        Assert.Equal(4, observations.Count);
        Assert.True(observations[2].CapturedAtUtc > observations[3].CapturedAtUtc);
        Assert.All(
            observations,
            observation => Assert.Equal("historical-build", observation.BuildId));
        Assert.Equal("C:\\observation-c", observations[0].InstallationRoot);
        Assert.Equal(
            "C:\\observation-c\\GameAssembly.dll",
            observations[0].GameAssemblyPath);
        Assert.Equal(
            "C:\\observation-c\\global-metadata.dat",
            observations[0].GlobalMetadataPath);
    }

    [Fact]
    public async Task SaveInputSnapshotAsync_WhenAFileInsertFails_RollsBackHeaderAndFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await CreateFailingSnapshotFileTriggerAsync(cancellationToken);
        var snapshot = CreateInputSnapshot(
            "snapshot-atomic",
            "build-a",
            replayVerified: true,
            includeFailingFile: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveInputSnapshotAsync(snapshot, cancellationToken));

        Assert.Equal(0L, await CountAsync("input_snapshots", cancellationToken));
        Assert.Equal(0L, await CountAsync("input_snapshot_files", cancellationToken));
    }

    [Fact]
    public async Task SaveInputSnapshotAsync_IdenticalDuplicateIsNoOpButDifferentFactsAreRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateInputSnapshot(
            "snapshot-idempotent",
            "build-a",
            replayVerified: true);
        var identical = CreateInputSnapshot(
            "snapshot-idempotent",
            "build-a",
            replayVerified: true);

        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        await repository.SaveInputSnapshotAsync(identical, cancellationToken);

        Assert.Equal(1L, await CountAsync("input_snapshots", cancellationToken));
        Assert.Equal(2L, await CountAsync("input_snapshot_files", cancellationToken));
        var replaySnapshots = await repository.ListReplayVerifiedInputSnapshotsAsync(
            "build-a",
            cancellationToken);
        var stored = Assert.Single(replaySnapshots);
        Assert.Equal(snapshot.InputSnapshotId, stored.InputSnapshotId);
        Assert.Equal(snapshot.BuildId, stored.BuildId);
        Assert.Equal(snapshot.RootPath, stored.RootPath);
        Assert.Equal(snapshot.ManifestDigest, stored.ManifestDigest);
        Assert.Equal(snapshot.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.True(stored.ReplayVerified);
        Assert.Equal(snapshot.ReplayVerifiedAtUtc, stored.ReplayVerifiedAtUtc);
        Assert.Equal(
            ["GameAssembly.dll", "global-metadata.dat"],
            stored.Manifest.Entries.Select(entry => entry.RelativePath));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveInputSnapshotAsync(
                identical with { RootPath = "C:\\different-root" },
                cancellationToken));
        Assert.Equal(1L, await CountAsync("input_snapshots", cancellationToken));
        Assert.Equal(2L, await CountAsync("input_snapshot_files", cancellationToken));
    }

    [Fact]
    public async Task GetAttemptAsync_StoredEnumCasingDiffers_RejectsCorruptRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var attempt = CreateAttempt("00000000000000000000000000000008");
        await repository.CreateAttemptAsync(attempt, cancellationToken);
        await SetRawStatusAsync(attempt.AttemptId, "created", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetAttemptAsync(attempt.AttemptId, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SqliteAtlasRepository> CreateInitializedRepositoryAsync(
        CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(
            _databasePath,
            _backupDirectory);
        await repository.InitializeAsync(cancellationToken);
        return repository;
    }

    private static async Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        string buildId,
        CancellationToken cancellationToken)
    {
        var timestamp = buildId == "historical-build"
            ? DateTimeOffset.Parse("2026-08-12T01:00:00Z")
            : DateTimeOffset.Parse("2026-08-13T01:00:00Z");
        var root = Path.GetFullPath(Path.Combine("C:\\games", buildId));
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(
                    buildId,
                    $"assembly-{buildId}",
                    $"metadata-{buildId}",
                    timestamp,
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
                CapturedAtUtc: timestamp),
            cancellationToken);
    }

    private static ExtractionAttempt CreateAttempt(string attemptId) =>
        new(
            AttemptId: attemptId,
            RecipeId: null,
            BuildId: "build-a",
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
            CreatedAtUtc: DateTimeOffset.Parse("2026-08-13T03:00:00Z"),
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: "C:\\attempts\\work",
            StandardOutputPath: "C:\\attempts\\stdout.log",
            StandardErrorPath: "C:\\attempts\\stderr.log",
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

    private static InputSnapshot CreateInputSnapshot(
        string snapshotId,
        string buildId,
        bool replayVerified,
        bool includeFailingFile = false)
    {
        var entries = new List<InputManifestEntry>
        {
            new(
                "global-metadata.dat",
                "Metadata",
                20,
                new string('2', 64),
                DateTimeOffset.Parse("2026-08-13T02:00:00Z")),
            new(
                "GameAssembly.dll",
                "GameAssembly",
                10,
                new string('1', 64),
                DateTimeOffset.Parse("2026-08-13T02:00:00Z"))
        };
        if (includeFailingFile)
        {
            entries.Add(new InputManifestEntry(
                "bad.dll",
                "FailureProbe",
                1,
                new string('3', 64),
                DateTimeOffset.Parse("2026-08-13T02:00:00Z")));
        }

        var createdAt = DateTimeOffset.Parse("2026-08-13T02:30:00Z");
        return new InputSnapshot(
            snapshotId,
            buildId,
            "C:\\snapshots\\input",
            new string('f', 64),
            createdAt,
            replayVerified,
            replayVerified ? createdAt.AddMinutes(1) : null,
            new InputManifest(entries));
    }

    private async Task SeedToolInstanceAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            INSERT INTO tool_instances (
                tool_instance_id,
                tool_name,
                version_label,
                platform,
                trust_level,
                definition_digest,
                package_sha256,
                executable_sha256,
                observed_path,
                first_observed_at_utc,
                last_verified_at_utc,
                status)
            VALUES (
                'tool-instance-1', 'cpp2il', 'test-version', 'win-x64',
                'ManagedPinned', 'definition', 'package', 'executable',
                'C:\tools\Cpp2IL.exe',
                '2026-08-13T01:00:00.0000000+00:00',
                '2026-08-13T02:00:00.0000000+00:00', 'Verified');
            """,
            cancellationToken);
    }

    private async Task InsertObservationAsync(
        string snapshotId,
        string buildId,
        string capturedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO environment_snapshots (
                snapshot_id,
                build_id,
                atlas_version,
                captured_at_utc,
                identity_version,
                executable_version,
                steam_app_id,
                steam_build_id,
                installation_root,
                game_assembly_path,
                global_metadata_path)
            VALUES (
                $snapshotId, $buildId, '0.2.0-test', $capturedAtUtc, 2,
                '2022.3', '3164500', '123', $installationRoot,
                $gameAssemblyPath, $globalMetadataPath);
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$capturedAtUtc", capturedAtUtc);
        command.Parameters.AddWithValue("$installationRoot", $"C:\\{snapshotId}");
        command.Parameters.AddWithValue(
            "$gameAssemblyPath",
            $"C:\\{snapshotId}\\GameAssembly.dll");
        command.Parameters.AddWithValue(
            "$globalMetadataPath",
            $"C:\\{snapshotId}\\global-metadata.dat");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CreateFailingSnapshotFileTriggerAsync(
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_snapshot_file
            BEFORE INSERT ON input_snapshot_files
            WHEN NEW.relative_path = 'bad.dll'
            BEGIN
                SELECT RAISE(ABORT, 'forced snapshot file failure');
            END;
            """,
            cancellationToken);

    private async Task SetStatusAsync(
        string attemptId,
        ExtractionAttemptStatus status,
        CancellationToken cancellationToken) =>
        await SetRawStatusAsync(attemptId, status.ToString(), cancellationToken);

    private async Task SetRawStatusAsync(
        string attemptId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE extraction_attempts
            SET status = $status
            WHERE attempt_id = $attemptId;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$attemptId", attemptId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
