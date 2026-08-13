using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    private const string AttemptColumns = """
        attempt_id,
        recipe_id,
        build_id,
        tool_instance_id,
        profile_id,
        profile_version,
        profile_digest,
        validation_policy_id,
        validation_policy_version,
        validation_policy_digest,
        adapter_version,
        extraction_schema_version,
        input_source,
        input_snapshot_id,
        status,
        created_at_utc,
        started_at_utc,
        completed_at_utc,
        pre_input_manifest_digest,
        post_input_manifest_digest,
        working_path,
        stdout_path,
        stderr_path,
        stdout_truncated,
        stderr_truncated,
        stdout_discarded_bytes,
        stderr_discarded_bytes,
        process_id,
        process_exit_code,
        failure_stage,
        failure_code,
        failure_message,
        keep_failed_artifacts,
        discarded_file_count,
        discarded_byte_count,
        candidate_output_path,
        result_extraction_id
        """;

    public async Task<GameBuild?> GetBuildAsync(
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                build_id,
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid
            FROM builds
            WHERE build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadBuild(reader)
            : null;
    }

    public async Task<IReadOnlyList<InstallationObservationRecord>>
        ListInstallationObservationsAsync(
            string buildId,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                snapshot_id,
                build_id,
                captured_at_utc,
                installation_root,
                game_assembly_path,
                global_metadata_path
            FROM environment_snapshots
            WHERE build_id = $buildId
            ORDER BY captured_at_utc DESC, snapshot_id DESC;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);

        var observations = new List<InstallationObservationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            observations.Add(new InstallationObservationRecord(
                reader.GetString(0),
                reader.GetString(1),
                ParseStoredTimestamp(
                    reader.GetString(2),
                    "environment_snapshots.captured_at_utc"),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return observations;
    }

    public async Task CreateAttemptAsync(
        ExtractionAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Status != ExtractionAttemptStatus.Created)
        {
            throw new InvalidOperationException(
                "New extraction attempts must be persisted in the Created state.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO extraction_attempts ({AttemptColumns})
            VALUES (
                $attemptId,
                $recipeId,
                $buildId,
                $toolInstanceId,
                $profileId,
                $profileVersion,
                $profileDigest,
                $validationPolicyId,
                $validationPolicyVersion,
                $validationPolicyDigest,
                $adapterVersion,
                $extractionSchemaVersion,
                $inputSource,
                $inputSnapshotId,
                $status,
                $createdAtUtc,
                $startedAtUtc,
                $completedAtUtc,
                $preInputManifestDigest,
                $postInputManifestDigest,
                $workingPath,
                $stdoutPath,
                $stderrPath,
                $stdoutTruncated,
                $stderrTruncated,
                $stdoutDiscardedBytes,
                $stderrDiscardedBytes,
                $processId,
                $processExitCode,
                $failureStage,
                $failureCode,
                $failureMessage,
                $keepFailedArtifacts,
                $discardedFileCount,
                $discardedByteCount,
                $candidateOutputPath,
                $resultExtractionId);
            """;
        AddAttemptParameters(command, attempt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidOperationException(
                $"Extraction attempt '{attempt.AttemptId}' could not be created.",
                exception);
        }
    }

    public async Task TransitionAttemptAsync(
        ExtractionAttempt attempt,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await GetAttemptAsync(
                connection,
                transaction,
                attempt.AttemptId,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Extraction attempt '{attempt.AttemptId}' does not exist.");
            ExtractionAttemptLifecycle.Transition(current, attempt);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE extraction_attempts
                SET recipe_id = $recipeId,
                    build_id = $buildId,
                    tool_instance_id = $toolInstanceId,
                    profile_id = $profileId,
                    profile_version = $profileVersion,
                    profile_digest = $profileDigest,
                    validation_policy_id = $validationPolicyId,
                    validation_policy_version = $validationPolicyVersion,
                    validation_policy_digest = $validationPolicyDigest,
                    adapter_version = $adapterVersion,
                    extraction_schema_version = $extractionSchemaVersion,
                    input_source = $inputSource,
                    input_snapshot_id = $inputSnapshotId,
                    status = $status,
                    created_at_utc = $createdAtUtc,
                    started_at_utc = $startedAtUtc,
                    completed_at_utc = $completedAtUtc,
                    pre_input_manifest_digest = $preInputManifestDigest,
                    post_input_manifest_digest = $postInputManifestDigest,
                    working_path = $workingPath,
                    stdout_path = $stdoutPath,
                    stderr_path = $stderrPath,
                    stdout_truncated = $stdoutTruncated,
                    stderr_truncated = $stderrTruncated,
                    stdout_discarded_bytes = $stdoutDiscardedBytes,
                    stderr_discarded_bytes = $stderrDiscardedBytes,
                    process_id = $processId,
                    process_exit_code = $processExitCode,
                    failure_stage = $failureStage,
                    failure_code = $failureCode,
                    failure_message = $failureMessage,
                    keep_failed_artifacts = $keepFailedArtifacts,
                    discarded_file_count = $discardedFileCount,
                    discarded_byte_count = $discardedByteCount,
                    candidate_output_path = $candidateOutputPath,
                    result_extraction_id = $resultExtractionId
                WHERE attempt_id = $attemptId
                  AND status = $expectedStatus;
                """;
            AddAttemptParameters(command, attempt);
            command.Parameters.AddWithValue("$expectedStatus", expectedStatus.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attempt.AttemptId}' lost an optimistic status transition from {expectedStatus}.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExtractionAttempt?> GetAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetAttemptAsync(
            connection,
            transaction: null,
            attemptId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AttemptColumns}
            FROM extraction_attempts
            WHERE status IN ('Created', 'Preparing', 'Running', 'Validating')
            ORDER BY created_at_utc, attempt_id;
            """;

        var attempts = new List<ExtractionAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    public async Task SaveInputSnapshotAsync(
        InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Manifest);
        ArgumentNullException.ThrowIfNull(snapshot.Manifest.Entries);
        var orderedFiles = snapshot.Manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await GetInputSnapshotAsync(
                connection,
                transaction,
                snapshot.InputSnapshotId,
                cancellationToken);
            if (existing is not null)
            {
                if (!HasSamePersistedSnapshotFacts(existing, snapshot))
                {
                    throw new InvalidOperationException(
                        $"Input snapshot '{snapshot.InputSnapshotId}' already exists with different facts.");
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await InsertInputSnapshotAsync(
                connection,
                transaction,
                snapshot,
                cancellationToken);
            foreach (var file in orderedFiles)
            {
                await InsertInputSnapshotFileAsync(
                    connection,
                    transaction,
                    snapshot.InputSnapshotId,
                    file,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (InvalidOperationException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"Input snapshot '{snapshot.InputSnapshotId}' could not be saved atomically.",
                exception);
        }
    }

    public async Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var headers = new List<InputSnapshotHeader>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    input_snapshot_id,
                    build_id,
                    root_path,
                    manifest_digest,
                    created_at_utc,
                    replay_verified,
                    replay_verified_at_utc
                FROM input_snapshots
                WHERE build_id = $buildId
                  AND replay_verified = 1
                ORDER BY created_at_utc DESC, input_snapshot_id DESC;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add(ReadInputSnapshotHeader(reader));
            }
        }

        var snapshots = new List<InputSnapshot>(headers.Count);
        foreach (var header in headers)
        {
            snapshots.Add(await MaterializeInputSnapshotAsync(
                connection,
                transaction: null,
                header,
                cancellationToken));
        }

        return snapshots;
    }

    private static async Task<ExtractionAttempt?> GetAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {AttemptColumns}
            FROM extraction_attempts
            WHERE attempt_id = $attemptId;
            """;
        command.Parameters.AddWithValue("$attemptId", attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAttempt(reader)
            : null;
    }

    private static ExtractionAttempt ReadAttempt(SqliteDataReader reader) =>
        new(
            AttemptId: reader.GetString(0),
            RecipeId: ReadNullableString(reader, 1),
            BuildId: reader.GetString(2),
            ToolInstanceId: ReadNullableString(reader, 3),
            ProfileId: reader.GetString(4),
            ProfileVersion: reader.GetInt32(5),
            ProfileDigest: reader.GetString(6),
            ValidationPolicyId: reader.GetString(7),
            ValidationPolicyVersion: reader.GetInt32(8),
            ValidationPolicyDigest: reader.GetString(9),
            AdapterVersion: reader.GetInt32(10),
            ExtractionSchemaVersion: reader.GetInt32(11),
            InputSource: reader.IsDBNull(12)
                ? null
                : ParseExtractionEnum<ExtractionInputSource>(
                    reader.GetString(12),
                    "extraction_attempts.input_source"),
            InputSnapshotId: ReadNullableString(reader, 13),
            Status: ParseExtractionEnum<ExtractionAttemptStatus>(
                reader.GetString(14),
                "extraction_attempts.status"),
            CreatedAtUtc: ParseStoredTimestamp(
                reader.GetString(15),
                "extraction_attempts.created_at_utc"),
            StartedAtUtc: ReadNullableTimestamp(
                reader,
                16,
                "extraction_attempts.started_at_utc"),
            CompletedAtUtc: ReadNullableTimestamp(
                reader,
                17,
                "extraction_attempts.completed_at_utc"),
            PreInputManifestDigest: ReadNullableString(reader, 18),
            PostInputManifestDigest: ReadNullableString(reader, 19),
            WorkingPath: reader.GetString(20),
            StandardOutputPath: reader.GetString(21),
            StandardErrorPath: reader.GetString(22),
            StandardOutputTruncated: reader.GetInt64(23) == 1,
            StandardErrorTruncated: reader.GetInt64(24) == 1,
            StandardOutputDiscardedBytes: reader.GetInt64(25),
            StandardErrorDiscardedBytes: reader.GetInt64(26),
            ProcessId: reader.IsDBNull(27) ? null : reader.GetInt32(27),
            ProcessExitCode: reader.IsDBNull(28) ? null : reader.GetInt32(28),
            FailureStage: reader.IsDBNull(29)
                ? null
                : ParseExtractionEnum<ExtractionFailureStage>(
                    reader.GetString(29),
                    "extraction_attempts.failure_stage"),
            FailureCode: reader.IsDBNull(30)
                ? null
                : ParseExtractionEnum<ExtractionFailureCode>(
                    reader.GetString(30),
                    "extraction_attempts.failure_code"),
            FailureMessage: ReadNullableString(reader, 31),
            KeepFailedArtifacts: reader.GetInt64(32) == 1,
            DiscardedFileCount: reader.GetInt32(33),
            DiscardedByteCount: reader.GetInt64(34),
            CandidateOutputPath: ReadNullableString(reader, 35),
            ResultExtractionId: ReadNullableString(reader, 36));

    private static void AddAttemptParameters(
        SqliteCommand command,
        ExtractionAttempt attempt)
    {
        command.Parameters.AddWithValue("$attemptId", attempt.AttemptId);
        AddNullableParameter(command, "$recipeId", attempt.RecipeId);
        command.Parameters.AddWithValue("$buildId", attempt.BuildId);
        AddNullableParameter(command, "$toolInstanceId", attempt.ToolInstanceId);
        command.Parameters.AddWithValue("$profileId", attempt.ProfileId);
        command.Parameters.AddWithValue("$profileVersion", attempt.ProfileVersion);
        command.Parameters.AddWithValue("$profileDigest", attempt.ProfileDigest);
        command.Parameters.AddWithValue(
            "$validationPolicyId",
            attempt.ValidationPolicyId);
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
        AddNullableParameter(
            command,
            "$inputSource",
            attempt.InputSource?.ToString());
        AddNullableParameter(command, "$inputSnapshotId", attempt.InputSnapshotId);
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(attempt.CreatedAtUtc));
        AddNullableTimestampParameter(command, "$startedAtUtc", attempt.StartedAtUtc);
        AddNullableTimestampParameter(
            command,
            "$completedAtUtc",
            attempt.CompletedAtUtc);
        AddNullableParameter(
            command,
            "$preInputManifestDigest",
            attempt.PreInputManifestDigest);
        AddNullableParameter(
            command,
            "$postInputManifestDigest",
            attempt.PostInputManifestDigest);
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
        AddNullableParameter(command, "$processId", attempt.ProcessId);
        AddNullableParameter(command, "$processExitCode", attempt.ProcessExitCode);
        AddNullableParameter(
            command,
            "$failureStage",
            attempt.FailureStage?.ToString());
        AddNullableParameter(
            command,
            "$failureCode",
            attempt.FailureCode?.ToString());
        AddNullableParameter(command, "$failureMessage", attempt.FailureMessage);
        command.Parameters.AddWithValue(
            "$keepFailedArtifacts",
            attempt.KeepFailedArtifacts ? 1 : 0);
        command.Parameters.AddWithValue(
            "$discardedFileCount",
            attempt.DiscardedFileCount);
        command.Parameters.AddWithValue(
            "$discardedByteCount",
            attempt.DiscardedByteCount);
        AddNullableParameter(
            command,
            "$candidateOutputPath",
            attempt.CandidateOutputPath);
        AddNullableParameter(
            command,
            "$resultExtractionId",
            attempt.ResultExtractionId);
    }

    private static void AddNullableTimestampParameter(
        SqliteCommand command,
        string name,
        DateTimeOffset? timestamp) =>
        AddNullableParameter(
            command,
            name,
            timestamp is null ? null : FormatTimestamp(timestamp.Value));

    private static void AddNullableParameter(
        SqliteCommand command,
        string name,
        object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string fieldName) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseStoredTimestamp(reader.GetString(ordinal), fieldName);

    private static TEnum ParseExtractionEnum<TEnum>(
        string value,
        string fieldName)
        where TEnum : struct, Enum
    {
        try
        {
            var parsed = Enum.Parse<TEnum>(value, ignoreCase: false);
            if (!Enum.IsDefined(parsed))
            {
                throw new ArgumentException("The enum value is not defined.", nameof(value));
            }

            return parsed;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Stored field '{fieldName}' contains unknown enum value '{value}'.",
                exception);
        }
    }

    private static async Task InsertInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO input_snapshots (
                input_snapshot_id,
                build_id,
                root_path,
                manifest_digest,
                created_at_utc,
                replay_verified,
                replay_verified_at_utc)
            VALUES (
                $inputSnapshotId,
                $buildId,
                $rootPath,
                $manifestDigest,
                $createdAtUtc,
                $replayVerified,
                $replayVerifiedAtUtc);
            """;
        command.Parameters.AddWithValue(
            "$inputSnapshotId",
            snapshot.InputSnapshotId);
        command.Parameters.AddWithValue("$buildId", snapshot.BuildId);
        command.Parameters.AddWithValue("$rootPath", snapshot.RootPath);
        command.Parameters.AddWithValue("$manifestDigest", snapshot.ManifestDigest);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(snapshot.CreatedAtUtc));
        command.Parameters.AddWithValue(
            "$replayVerified",
            snapshot.ReplayVerified ? 1 : 0);
        AddNullableTimestampParameter(
            command,
            "$replayVerifiedAtUtc",
            snapshot.ReplayVerifiedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInputSnapshotFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string inputSnapshotId,
        InputManifestEntry file,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO input_snapshot_files (
                input_snapshot_id,
                relative_path,
                role,
                size,
                sha256)
            VALUES (
                $inputSnapshotId,
                $relativePath,
                $role,
                $size,
                $sha256);
            """;
        command.Parameters.AddWithValue("$inputSnapshotId", inputSnapshotId);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath);
        command.Parameters.AddWithValue("$role", file.Role);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$sha256", file.Sha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InputSnapshot?> GetInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string inputSnapshotId,
        CancellationToken cancellationToken)
    {
        InputSnapshotHeader? header;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    input_snapshot_id,
                    build_id,
                    root_path,
                    manifest_digest,
                    created_at_utc,
                    replay_verified,
                    replay_verified_at_utc
                FROM input_snapshots
                WHERE input_snapshot_id = $inputSnapshotId;
                """;
            command.Parameters.AddWithValue("$inputSnapshotId", inputSnapshotId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? ReadInputSnapshotHeader(reader)
                : null;
        }

        return header is null
            ? null
            : await MaterializeInputSnapshotAsync(
                connection,
                transaction,
                header,
                cancellationToken);
    }

    private static InputSnapshotHeader ReadInputSnapshotHeader(
        SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseStoredTimestamp(
                reader.GetString(4),
                "input_snapshots.created_at_utc"),
            reader.GetInt64(5) == 1,
            ReadNullableTimestamp(
                reader,
                6,
                "input_snapshots.replay_verified_at_utc"));

    private static async Task<InputSnapshot> MaterializeInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        InputSnapshotHeader header,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT relative_path, role, size, sha256
            FROM input_snapshot_files
            WHERE input_snapshot_id = $inputSnapshotId
            ORDER BY relative_path;
            """;
        command.Parameters.AddWithValue("$inputSnapshotId", header.InputSnapshotId);
        var files = new List<InputManifestEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new InputManifestEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                header.CreatedAtUtc));
        }

        return new InputSnapshot(
            header.InputSnapshotId,
            header.BuildId,
            header.RootPath,
            header.ManifestDigest,
            header.CreatedAtUtc,
            header.ReplayVerified,
            header.ReplayVerifiedAtUtc,
            new InputManifest(files));
    }

    private static bool HasSamePersistedSnapshotFacts(
        InputSnapshot existing,
        InputSnapshot candidate)
    {
        if (!string.Equals(
                existing.InputSnapshotId,
                candidate.InputSnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(existing.BuildId, candidate.BuildId, StringComparison.Ordinal) ||
            !string.Equals(existing.RootPath, candidate.RootPath, StringComparison.Ordinal) ||
            !string.Equals(
                existing.ManifestDigest,
                candidate.ManifestDigest,
                StringComparison.Ordinal) ||
            existing.CreatedAtUtc != candidate.CreatedAtUtc ||
            existing.ReplayVerified != candidate.ReplayVerified ||
            existing.ReplayVerifiedAtUtc != candidate.ReplayVerifiedAtUtc)
        {
            return false;
        }

        var existingFiles = existing.Manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var candidateFiles = candidate.Manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return existingFiles.Length == candidateFiles.Length &&
            existingFiles.Zip(candidateFiles).All(pair =>
                string.Equals(
                    pair.First.RelativePath,
                    pair.Second.RelativePath,
                    StringComparison.Ordinal) &&
                string.Equals(
                    pair.First.Role,
                    pair.Second.Role,
                    StringComparison.Ordinal) &&
                pair.First.Size == pair.Second.Size &&
                string.Equals(
                    pair.First.Sha256,
                    pair.Second.Sha256,
                    StringComparison.Ordinal));
    }

    private sealed record InputSnapshotHeader(
        string InputSnapshotId,
        string BuildId,
        string RootPath,
        string ManifestDigest,
        DateTimeOffset CreatedAtUtc,
        bool ReplayVerified,
        DateTimeOffset? ReplayVerifiedAtUtc);
}
