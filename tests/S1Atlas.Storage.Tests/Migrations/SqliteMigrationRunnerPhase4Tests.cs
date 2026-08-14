using Microsoft.Data.Sqlite;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

/// <summary>
/// Phase 4 migration coverage remains valid as later migrations append: the
/// validated-extractions migration must preserve every Phase 3 row, current
/// initialization must add exactly one backup on an existing v4 database, add
/// none on a brand-new database, and leave migrations 1-4's committed checksums
/// byte-for-byte unchanged.
/// </summary>
public sealed class SqliteMigrationRunnerPhase4Tests : IAsyncDisposable
{
    private const string RealisticAttemptId = "00000000000000000000000000000009";
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(
        DateTimeOffset.Parse("2026-08-13T04:00:00Z"));

    public SqliteMigrationRunnerPhase4Tests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-phase4-migration-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task MigrateAsync_V4Database_AddsValidatedExtractionTablesAndOneSchema7Backup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateVersionFourDatabaseWithRealisticAttemptAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(_databasePath, _backupDirectory);

        await repository.InitializeAsync(cancellationToken);

        var migrationVersions = await ReadMigrationVersionsAsync(cancellationToken);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], migrationVersions);
        Assert.True(await TableExistsAsync("validated_extractions", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_artifacts", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_validation_results", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_validation_issues", cancellationToken));
        Assert.True(await TableExistsAsync("preferred_extractions", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_preference_events", cancellationToken));
        Assert.True(await ColumnExistsAsync(
            "extraction_attempts",
            "validation_source_extraction_id",
            cancellationToken));
        Assert.True(await ColumnExistsAsync(
            "symbols",
            "body_recovery_status",
            cancellationToken));
        Assert.Single(GetSchemaSevenBackups());
    }

    [Fact]
    public async Task MigrateAsync_NewDatabase_AppliesSevenMigrationsWithoutBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runner = new SqliteMigrationRunner(_databasePath, _backupDirectory, _timeProvider);

        await runner.MigrateAsync(cancellationToken);

        var migrationVersions = await ReadMigrationVersionsAsync(cancellationToken);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], migrationVersions);
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task MigrateAsync_ExistingPhase3Candidate_PreservesAttemptAndAddsNullableValidationSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expectedAttempt = await CreateVersionFourDatabaseWithRealisticAttemptAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(_databasePath, _backupDirectory);

        await repository.InitializeAsync(cancellationToken);
        var reloaded = await repository.GetAttemptAsync(RealisticAttemptId, cancellationToken);

        Assert.NotNull(reloaded);
        Assert.Equal(expectedAttempt, reloaded);
        Assert.Null(reloaded.ValidationSourceExtractionId);
        Assert.Equal(ExtractionAttemptStatus.ProcessCompleted, reloaded.Status);
        Assert.Equal("C:\\attempts\\candidate", reloaded.CandidateOutputPath);
    }

    [Fact]
    public void MigrateAsync_MigrationsOneThroughFourKeepCommittedChecksums()
    {
        Assert.Equal(
            [
                "90ee69e49a9763c6443b4db0b5b2752ff78292fb7a7f7e7b5d86fd22137fd92e",
                "39cb7f3c2c6fa047e718b101da950ea39da03277a1763b1ba14d4abd79c519ae",
                "c730f9db46ae1565f82df2cde3651f27e3c6f835d85f68b2e52f72fe36e8ebea",
                "e735858f725c4c285edc82a6170d9fdb3f5161eb960f87f8cece165e416c899d"
            ],
            SqliteMigrations.All.Take(4).Select(migration => migration.Checksum));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds a v4-only database (migrations 1-4, matching
    /// <see cref="ExtractionAttemptMigrationTests"/>'s pattern) seeded with a build,
    /// a managed tool instance, and a realistic <c>ProcessCompleted</c> attempt whose
    /// row predates the <c>validation_source_extraction_id</c> column.
    /// </summary>
    private async Task<ExtractionAttempt> CreateVersionFourDatabaseWithRealisticAttemptAsync(
        CancellationToken cancellationToken)
    {
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            SqliteMigrations.All.Take(4).ToArray(),
            _timeProvider);
        await runner.MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO builds (
                build_id, game_assembly_sha256, metadata_sha256, first_seen_at_utc, is_valid)
            VALUES (
                'build-a', 'assembly-a', 'metadata-a',
                '2026-08-13T00:00:00.0000000+00:00', 1);

            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                'tool-instance-1', 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-13T00:00:00.0000000+00:00',
                '2026-08-13T00:30:00.0000000+00:00', 'Verified');
            """,
            cancellationToken);

        var attempt = new ExtractionAttempt(
            AttemptId: RealisticAttemptId,
            RecipeId: "recipe-1",
            BuildId: "build-a",
            ToolInstanceId: "tool-instance-1",
            ProfileId: "default",
            ProfileVersion: 1,
            ProfileDigest: new string('a', 64),
            ValidationPolicyId: "managed-assemblies-v1",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: new string('b', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: ExtractionInputSource.Live,
            InputSnapshotId: null,
            Status: ExtractionAttemptStatus.ProcessCompleted,
            CreatedAtUtc: DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            StartedAtUtc: DateTimeOffset.Parse("2026-08-13T01:01:00Z"),
            CompletedAtUtc: DateTimeOffset.Parse("2026-08-13T01:05:00Z"),
            PreInputManifestDigest: new string('c', 64),
            PostInputManifestDigest: new string('d', 64),
            WorkingPath: "C:\\attempts\\work",
            StandardOutputPath: "C:\\attempts\\stdout.log",
            StandardErrorPath: "C:\\attempts\\stderr.log",
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: 4242,
            ProcessExitCode: 0,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            KeepFailedArtifacts: false,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: "C:\\attempts\\candidate",
            ResultExtractionId: null);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO extraction_attempts (
                    attempt_id, recipe_id, build_id, tool_instance_id, profile_id,
                    profile_version, profile_digest, validation_policy_id,
                    validation_policy_version, validation_policy_digest,
                    adapter_version, extraction_schema_version, input_source,
                    input_snapshot_id, status, created_at_utc, started_at_utc,
                    completed_at_utc, pre_input_manifest_digest,
                    post_input_manifest_digest, working_path, stdout_path,
                    stderr_path, stdout_truncated, stderr_truncated,
                    stdout_discarded_bytes, stderr_discarded_bytes, process_id,
                    process_exit_code, failure_stage, failure_code, failure_message,
                    keep_failed_artifacts, discarded_file_count, discarded_byte_count,
                    candidate_output_path, result_extraction_id)
                VALUES (
                    $attemptId, $recipeId, $buildId, $toolInstanceId, $profileId,
                    $profileVersion, $profileDigest, $validationPolicyId,
                    $validationPolicyVersion, $validationPolicyDigest,
                    $adapterVersion, $extractionSchemaVersion, $inputSource,
                    $inputSnapshotId, $status, $createdAtUtc, $startedAtUtc,
                    $completedAtUtc, $preInputManifestDigest, $postInputManifestDigest,
                    $workingPath, $stdoutPath, $stderrPath, $stdoutTruncated,
                    $stderrTruncated, $stdoutDiscardedBytes, $stderrDiscardedBytes,
                    $processId, $processExitCode, $failureStage, $failureCode,
                    $failureMessage, $keepFailedArtifacts, $discardedFileCount,
                    $discardedByteCount, $candidateOutputPath, $resultExtractionId);
                """;
            command.Parameters.AddWithValue("$attemptId", attempt.AttemptId);
            command.Parameters.AddWithValue("$recipeId", attempt.RecipeId!);
            command.Parameters.AddWithValue("$buildId", attempt.BuildId);
            command.Parameters.AddWithValue("$toolInstanceId", attempt.ToolInstanceId!);
            command.Parameters.AddWithValue("$profileId", attempt.ProfileId);
            command.Parameters.AddWithValue("$profileVersion", attempt.ProfileVersion);
            command.Parameters.AddWithValue("$profileDigest", attempt.ProfileDigest);
            command.Parameters.AddWithValue("$validationPolicyId", attempt.ValidationPolicyId);
            command.Parameters.AddWithValue(
                "$validationPolicyVersion",
                attempt.ValidationPolicyVersion);
            command.Parameters.AddWithValue(
                "$validationPolicyDigest",
                attempt.ValidationPolicyDigest);
            command.Parameters.AddWithValue("$adapterVersion", attempt.AdapterVersion);
            command.Parameters.AddWithValue(
                "$extractionSchemaVersion",
                attempt.ExtractionSchemaVersion);
            command.Parameters.AddWithValue("$inputSource", attempt.InputSource!.ToString()!);
            command.Parameters.AddWithValue("$inputSnapshotId", DBNull.Value);
            command.Parameters.AddWithValue("$status", attempt.Status.ToString());
            command.Parameters.AddWithValue(
                "$createdAtUtc",
                attempt.CreatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue(
                "$startedAtUtc",
                attempt.StartedAtUtc!.Value.ToString("O"));
            command.Parameters.AddWithValue(
                "$completedAtUtc",
                attempt.CompletedAtUtc!.Value.ToString("O"));
            command.Parameters.AddWithValue(
                "$preInputManifestDigest",
                attempt.PreInputManifestDigest!);
            command.Parameters.AddWithValue(
                "$postInputManifestDigest",
                attempt.PostInputManifestDigest!);
            command.Parameters.AddWithValue("$workingPath", attempt.WorkingPath);
            command.Parameters.AddWithValue("$stdoutPath", attempt.StandardOutputPath);
            command.Parameters.AddWithValue("$stderrPath", attempt.StandardErrorPath);
            command.Parameters.AddWithValue(
                "$stdoutTruncated",
                attempt.StandardOutputTruncated ? 1 : 0);
            command.Parameters.AddWithValue(
                "$stderrTruncated",
                attempt.StandardErrorTruncated ? 1 : 0);
            command.Parameters.AddWithValue(
                "$stdoutDiscardedBytes",
                attempt.StandardOutputDiscardedBytes);
            command.Parameters.AddWithValue(
                "$stderrDiscardedBytes",
                attempt.StandardErrorDiscardedBytes);
            command.Parameters.AddWithValue("$processId", attempt.ProcessId!.Value);
            command.Parameters.AddWithValue("$processExitCode", attempt.ProcessExitCode!.Value);
            command.Parameters.AddWithValue("$failureStage", DBNull.Value);
            command.Parameters.AddWithValue("$failureCode", DBNull.Value);
            command.Parameters.AddWithValue("$failureMessage", DBNull.Value);
            command.Parameters.AddWithValue(
                "$keepFailedArtifacts",
                attempt.KeepFailedArtifacts ? 1 : 0);
            command.Parameters.AddWithValue(
                "$discardedFileCount",
                attempt.DiscardedFileCount);
            command.Parameters.AddWithValue(
                "$discardedByteCount",
                attempt.DiscardedByteCount);
            command.Parameters.AddWithValue(
                "$candidateOutputPath",
                attempt.CandidateOutputPath!);
            command.Parameters.AddWithValue("$resultExtractionId", DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return attempt;
    }

    private string[] GetSchemaSevenBackups() =>
        Directory.Exists(_backupDirectory)
            ? Directory.GetFiles(
                _backupDirectory,
                "atlas-before-schema-7-*.db",
                SearchOption.TopDirectoryOnly)
            : [];

    private async Task<int[]> ReadMigrationVersionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        var versions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions.ToArray();
    }

    private async Task<bool> TableExistsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<bool> ColumnExistsAsync(
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}