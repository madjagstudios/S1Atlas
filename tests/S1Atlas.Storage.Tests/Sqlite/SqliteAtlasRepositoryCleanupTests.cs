using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryCleanupTests : IAsyncDisposable
{
    private static readonly DateTimeOffset CompletedAt =
        DateTimeOffset.Parse("2026-08-13T03:02:00.0000000+00:00");

    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryCleanupTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-cleanup-repository-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Canceled)]
    [InlineData(ExtractionAttemptStatus.Abandoned)]
    public async Task DeleteCleanupEligibleAttemptAsync_TerminalAttempt_DeletesValidationRowsIssuesAndAttempt(
        ExtractionAttemptStatus status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000001", status, CompletedAt, cancellationToken);
        await InsertValidationResultRowAsync(
            "00000000000000000000000000000001", cancellationToken);
        await InsertValidationIssueRowAsync(
            "00000000000000000000000000000001", cancellationToken);
        // A protected neighbour that must remain untouched.
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000002",
            ExtractionAttemptStatus.Succeeded, CompletedAt, cancellationToken);
        await InsertValidationResultRowAsync(
            "00000000000000000000000000000002", cancellationToken);

        await repository.DeleteCleanupEligibleAttemptAsync(
            "00000000000000000000000000000001", status, CompletedAt, cancellationToken);

        Assert.Null(await repository.GetAttemptAsync(
            "00000000000000000000000000000001", cancellationToken));
        Assert.Equal(0L, await CountWhereAttemptAsync(
            "extraction_validation_results", "00000000000000000000000000000001", cancellationToken));
        Assert.Equal(0L, await CountWhereAttemptAsync(
            "extraction_validation_issues", "00000000000000000000000000000001", cancellationToken));
        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000002", cancellationToken));
        Assert.Equal(1L, await CountWhereAttemptAsync(
            "extraction_validation_results", "00000000000000000000000000000002", cancellationToken));
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.ProcessCompleted)]
    [InlineData(ExtractionAttemptStatus.Succeeded)]
    [InlineData(ExtractionAttemptStatus.Created)]
    public async Task DeleteCleanupEligibleAttemptAsync_ProtectedStatus_RejectsAndPreserves(
        ExtractionAttemptStatus status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000003", status, CompletedAt, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000003", status, CompletedAt, cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000003", cancellationToken));
    }

    [Fact]
    public async Task DeleteCleanupEligibleAttemptAsync_ReferencedByValidatedExtraction_Rejects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await SeedToolInstanceAsync(cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000004",
            ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken);
        await InsertValidatedExtractionReferencingAttemptAsync(
            "extraction-1", "00000000000000000000000000000004", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000004",
                ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000004", cancellationToken));
    }

    [Fact]
    public async Task DeleteCleanupEligibleAttemptAsync_WithResultExtractionId_Rejects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000005",
            ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken,
            resultExtractionId: "extraction-x");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000005",
                ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000005", cancellationToken));
    }

    [Fact]
    public async Task DeleteCleanupEligibleAttemptAsync_ExpectedStatusMismatch_Rejects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000006",
            ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000006",
                ExtractionAttemptStatus.Canceled, CompletedAt, cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000006", cancellationToken));
    }

    [Fact]
    public async Task DeleteCleanupEligibleAttemptAsync_ExpectedTimestampMismatch_Rejects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000007",
            ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000007",
                ExtractionAttemptStatus.Failed,
                CompletedAt.AddMinutes(1),
                cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000007", cancellationToken));
    }

    [Fact]
    public async Task DeleteCleanupEligibleAttemptAsync_ForcedFailureAfterValidationDeletion_RollsBackEveryRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        await InsertTerminalAttemptAsync(
            repository, "00000000000000000000000000000008",
            ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken);
        await InsertValidationResultRowAsync(
            "00000000000000000000000000000008", cancellationToken);
        await InsertValidationIssueRowAsync(
            "00000000000000000000000000000008", cancellationToken);
        // Abort the final attempt delete after the validation rows are already deleted
        // inside the transaction; the whole transaction must roll back.
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_attempt_delete
            BEFORE DELETE ON extraction_attempts
            WHEN OLD.attempt_id = '00000000000000000000000000000008'
            BEGIN
                SELECT RAISE(ABORT, 'forced attempt delete failure');
            END;
            """,
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteCleanupEligibleAttemptAsync(
                "00000000000000000000000000000008",
                ExtractionAttemptStatus.Failed, CompletedAt, cancellationToken));

        Assert.NotNull(await repository.GetAttemptAsync(
            "00000000000000000000000000000008", cancellationToken));
        Assert.Equal(1L, await CountWhereAttemptAsync(
            "extraction_validation_results", "00000000000000000000000000000008", cancellationToken));
        Assert.Equal(1L, await CountWhereAttemptAsync(
            "extraction_validation_issues", "00000000000000000000000000000008", cancellationToken));
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
        var repository = new SqliteAtlasRepository(_databasePath, _backupDirectory);
        await repository.InitializeAsync(cancellationToken);
        return repository;
    }

    private static async Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        string buildId,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-13T01:00:00Z");
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

    private async Task InsertTerminalAttemptAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        ExtractionAttemptStatus status,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken,
        string? resultExtractionId = null)
    {
        await repository.CreateAttemptAsync(CreateAttempt(attemptId), cancellationToken);
        var completed = status == ExtractionAttemptStatus.Created
            ? (DateTimeOffset?)null
            : completedAtUtc;
        await ExecuteAsync(
            """
            UPDATE extraction_attempts
            SET status = $status,
                completed_at_utc = $completedAtUtc,
                result_extraction_id = $resultExtractionId
            WHERE attempt_id = $attemptId;
            """,
            cancellationToken,
            command =>
            {
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue(
                    "$completedAtUtc",
                    completed is null
                        ? DBNull.Value
                        : completed.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$resultExtractionId",
                    (object?)resultExtractionId ?? DBNull.Value);
                command.Parameters.AddWithValue("$attemptId", attemptId);
            });
    }

    private static ExtractionAttempt CreateAttempt(string attemptId) =>
        new(
            AttemptId: attemptId,
            RecipeId: "recipe-1",
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

    private async Task InsertValidationResultRowAsync(
        string attemptId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            INSERT INTO extraction_validation_results (
                attempt_id, subject_extraction_id, artifact_manifest_digest,
                policy_id, policy_version, policy_digest, outcome, report_path,
                baseline_extraction_id, preference_eligible, validated_at_utc,
                artifact_count, library_count, managed_assembly_count, type_count,
                method_count, field_count, property_count, event_count,
                total_output_bytes, total_managed_bytes)
            VALUES (
                $attemptId, NULL, $digest, 'default', 1, $digest, 'Invalid',
                'C:\attempts\validation.json', NULL, 0, '2026-08-13T03:02:00.0000000+00:00',
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            """,
            cancellationToken,
            command =>
            {
                command.Parameters.AddWithValue("$attemptId", attemptId);
                command.Parameters.AddWithValue("$digest", new string('c', 64));
            });

    private async Task InsertValidationIssueRowAsync(
        string attemptId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            INSERT INTO extraction_validation_issues (
                attempt_id, ordinal, severity, code, message,
                artifact_relative_path, preference_blocking)
            VALUES ($attemptId, 0, 'Error', 'E001', 'issue', NULL, 1);
            """,
            cancellationToken,
            command => command.Parameters.AddWithValue("$attemptId", attemptId));

    private async Task SeedToolInstanceAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                'tool-instance-1', 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-13T01:00:00.0000000+00:00',
                '2026-08-13T02:00:00.0000000+00:00', 'Verified');
            """,
            cancellationToken);

    private async Task InsertValidatedExtractionReferencingAttemptAsync(
        string extractionId,
        string sourceAttemptId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            INSERT INTO validated_extractions (
                extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id,
                profile_id, profile_version, profile_digest, adapter_version,
                extraction_schema_version, artifact_manifest_digest, root_path,
                created_at_utc, trust_level, validation_outcome, artifact_count,
                library_count, managed_assembly_count, type_count, method_count,
                field_count, property_count, event_count, total_output_bytes,
                total_managed_bytes)
            VALUES (
                $extractionId, 'recipe-1', 'build-a', 'tool-instance-1', $sourceAttemptId,
                'default', 1, $digest, 1, 1, $digest, 'C:\extractions\e1',
                '2026-08-13T03:03:00.0000000+00:00', 'ManagedPinned', 'Valid',
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            """,
            cancellationToken,
            command =>
            {
                command.Parameters.AddWithValue("$extractionId", extractionId);
                command.Parameters.AddWithValue("$sourceAttemptId", sourceAttemptId);
                command.Parameters.AddWithValue("$digest", new string('d', 64));
            });

    private async Task<long> CountWhereAttemptAsync(
        string tableName,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE attempt_id = $attemptId;";
        command.Parameters.AddWithValue("$attemptId", attemptId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        Action<SqliteCommand>? configure = null)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
