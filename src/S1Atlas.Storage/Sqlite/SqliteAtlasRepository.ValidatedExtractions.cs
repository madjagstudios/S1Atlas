using Microsoft.Data.Sqlite;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;

namespace S1Atlas.Storage.Sqlite;

/// <summary>
/// Phase 4 validated-extraction persistence: immutable extraction/artifact rows,
/// attempt-scoped validation history, and mutable per-build preference tracking.
/// Every multi-table write in this file is atomic: a failed insert anywhere in the
/// transaction rolls back the whole write, including any attempt-status transition
/// and preference change, so no partial rows or false preferences are ever visible.
/// </summary>
public sealed partial class SqliteAtlasRepository
{
    private const string ValidatedExtractionColumns = """
        extraction_id,
        recipe_id,
        build_id,
        tool_instance_id,
        source_attempt_id,
        profile_id,
        profile_version,
        profile_digest,
        adapter_version,
        extraction_schema_version,
        artifact_manifest_digest,
        root_path,
        created_at_utc,
        trust_level,
        validation_outcome,
        artifact_count,
        library_count,
        managed_assembly_count,
        type_count,
        method_count,
        field_count,
        property_count,
        event_count,
        total_output_bytes,
        total_managed_bytes
        """;

    public async Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AttemptColumns}
            FROM extraction_attempts
            WHERE recipe_id = $recipeId
              AND status = $status
            ORDER BY created_at_utc DESC, attempt_id DESC;
            """;
        command.Parameters.AddWithValue("$recipeId", recipeId);
        command.Parameters.AddWithValue(
            "$status",
            ExtractionAttemptStatus.ProcessCompleted.ToString());

        var attempts = new List<ExtractionAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    public async Task<ValidatedExtraction?> GetValidatedExtractionAsync(
        string extractionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var header = await ReadValidatedExtractionHeaderAsync(
            connection,
            transaction: null,
            extractionId,
            cancellationToken);
        return header is null
            ? null
            : await MaterializeValidatedExtractionAsync(
                connection,
                transaction: null,
                header,
                cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
        string extractionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                relative_path,
                kind,
                size,
                sha256,
                assembly_name,
                module_name,
                type_count,
                method_count,
                field_count,
                property_count,
                event_count
            FROM extraction_artifacts
            WHERE extraction_id = $extractionId
            ORDER BY relative_path;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);

        var artifacts = new List<ArtifactManifestEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(ReadArtifactEntry(reader));
        }

        return artifacts;
    }

    public async Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
        string? buildId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var headers = new List<ValidatedExtraction>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = buildId is null
                ? $"""
                  SELECT {ValidatedExtractionColumns}
                  FROM validated_extractions
                  ORDER BY created_at_utc DESC, extraction_id DESC;
                  """
                : $"""
                  SELECT {ValidatedExtractionColumns}
                  FROM validated_extractions
                  WHERE build_id = $buildId
                  ORDER BY created_at_utc DESC, extraction_id DESC;
                  """;
            if (buildId is not null)
            {
                command.Parameters.AddWithValue("$buildId", buildId);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add(ReadValidatedExtractionHeader(reader));
            }
        }

        return await MaterializeValidatedExtractionsAsync(connection, headers, cancellationToken);
    }

    public async Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var headers = new List<ValidatedExtraction>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {ValidatedExtractionColumns}
                FROM validated_extractions
                WHERE recipe_id = $recipeId
                ORDER BY created_at_utc DESC, extraction_id DESC;
                """;
            command.Parameters.AddWithValue("$recipeId", recipeId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add(ReadValidatedExtractionHeader(reader));
            }
        }

        return await MaterializeValidatedExtractionsAsync(connection, headers, cancellationToken);
    }

    public async Task<StoredValidationResult?> GetLatestValidationResultAsync(
        string extractionId,
        string policyDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDigest);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                attempt_id,
                subject_extraction_id,
                artifact_manifest_digest,
                policy_id,
                policy_version,
                policy_digest,
                outcome,
                report_path,
                baseline_extraction_id,
                preference_eligible,
                validated_at_utc,
                artifact_count,
                library_count,
                managed_assembly_count,
                type_count,
                method_count,
                field_count,
                property_count,
                event_count,
                total_output_bytes,
                total_managed_bytes
            FROM extraction_validation_results
            WHERE subject_extraction_id = $extractionId
              AND policy_digest = $policyDigest
            ORDER BY validated_at_utc DESC, attempt_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$policyDigest", policyDigest);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        // Per-assembly statistics live in validation.json and are not reconstructed from SQL.
        return new StoredValidationResult(
            AttemptId: reader.GetString(0),
            SubjectExtractionId: ReadNullableString(reader, 1),
            ArtifactManifestDigest: reader.GetString(2),
            PolicyId: reader.GetString(3),
            PolicyVersion: reader.GetInt32(4),
            PolicyDigest: reader.GetString(5),
            Outcome: ParseStoredEnum<ValidationOutcome>(
                reader.GetString(6),
                "extraction_validation_results.outcome"),
            ReportPath: reader.GetString(7),
            BaselineExtractionId: ReadNullableString(reader, 8),
            PreferenceEligible: reader.GetInt64(9) == 1,
            ValidatedAtUtc: ParseStoredTimestamp(
                reader.GetString(10),
                "extraction_validation_results.validated_at_utc"),
            Statistics: new ExtractionStatistics(
                ArtifactCount: reader.GetInt32(11),
                LibraryCount: reader.GetInt32(12),
                ManagedAssemblyCount: reader.GetInt32(13),
                TypeDefinitionCount: reader.GetInt32(14),
                MethodDefinitionCount: reader.GetInt32(15),
                FieldDefinitionCount: reader.GetInt32(16),
                PropertyDefinitionCount: reader.GetInt32(17),
                EventDefinitionCount: reader.GetInt32(18),
                TotalOutputBytes: reader.GetInt64(19),
                TotalManagedBytes: reader.GetInt64(20),
                Assemblies: []));
    }

    public async Task<PreferredExtraction?> GetPreferredExtractionAsync(
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadPreferredExtractionAsync(
            connection,
            transaction: null,
            buildId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
        string? buildId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = buildId is null
            ? $"""
              SELECT {AttemptColumns}
              FROM extraction_attempts
              ORDER BY created_at_utc DESC, attempt_id DESC;
              """
            : $"""
              SELECT {AttemptColumns}
              FROM extraction_attempts
              WHERE build_id = $buildId
              ORDER BY created_at_utc DESC, attempt_id DESC;
              """;
        if (buildId is not null)
        {
            command.Parameters.AddWithValue("$buildId", buildId);
        }

        var attempts = new List<ExtractionAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    public async Task SaveValidationFailureAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(validation.Attempt);
        ArgumentNullException.ThrowIfNull(validation.Report);
        if (validation.Attempt.Status != ExtractionAttemptStatus.Failed)
        {
            throw new ArgumentException(
                "A validation-failure persistence command must transition the " +
                "attempt to Failed.",
                nameof(validation));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await InsertValidationResultAsync(
                connection,
                transaction,
                validation.Attempt,
                validation.Report,
                cancellationToken);
            await InsertValidationIssuesAsync(
                connection,
                transaction,
                validation.Report,
                cancellationToken);
            await TransitionAttemptWithinTransactionAsync(
                connection,
                transaction,
                validation.Attempt,
                expectedStatus,
                cancellationToken);

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
                $"The validation failure for attempt '{validation.Attempt.AttemptId}' " +
                "could not be saved atomically.",
                exception);
        }
    }

    public async Task CommitValidatedExtractionAsync(
        ValidatedExtractionPromotion promotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ArgumentNullException.ThrowIfNull(promotion.Attempt);
        ArgumentNullException.ThrowIfNull(promotion.Extraction);
        ArgumentNullException.ThrowIfNull(promotion.ArtifactManifest);
        ArgumentNullException.ThrowIfNull(promotion.ArtifactManifest.Entries);
        ArgumentNullException.ThrowIfNull(promotion.ValidationReport);
        RequireLowerCaseHex(promotion.Extraction.ExtractionId, nameof(promotion.Extraction.ExtractionId));
        RequireLowerCaseHex(
            promotion.Extraction.ArtifactManifestDigest,
            nameof(promotion.Extraction.ArtifactManifestDigest));

        if (promotion.Attempt.Status != ExtractionAttemptStatus.Succeeded ||
            !string.Equals(
                promotion.Attempt.ResultExtractionId,
                promotion.Extraction.ExtractionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Committing a validated extraction requires the attempt to succeed " +
                "with a matching result extraction ID.",
                nameof(promotion));
        }

        if (!string.Equals(
                promotion.Attempt.AttemptId,
                promotion.Extraction.SourceAttemptId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The validated extraction's source attempt must match the " +
                "committing attempt.",
                nameof(promotion));
        }

        var orderedArtifacts = SortArtifactEntries(promotion.ArtifactManifest.Entries);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await InsertValidatedExtractionAsync(
                connection,
                transaction,
                promotion.Extraction,
                cancellationToken);
            await InsertExtractionArtifactsAsync(
                connection,
                transaction,
                promotion.Extraction.ExtractionId,
                orderedArtifacts,
                cancellationToken);
            await InsertValidationResultAsync(
                connection,
                transaction,
                promotion.Attempt,
                promotion.ValidationReport,
                cancellationToken);
            await InsertValidationIssuesAsync(
                connection,
                transaction,
                promotion.ValidationReport,
                cancellationToken);
            await TransitionAttemptWithinTransactionAsync(
                connection,
                transaction,
                promotion.Attempt,
                ExtractionAttemptStatus.Validating,
                cancellationToken);

            if (promotion.AutomaticPreferenceReason is { } reason)
            {
                var previous = await ReadPreferredExtractionAsync(
                    connection,
                    transaction,
                    promotion.Extraction.BuildId,
                    cancellationToken);
                var preference = new PreferredExtraction(
                    promotion.Extraction.BuildId,
                    promotion.Extraction.ExtractionId,
                    promotion.ValidationReport.ValidatedAtUtc,
                    reason);
                await UpsertPreferredExtractionAsync(
                    connection,
                    transaction,
                    preference,
                    cancellationToken);
                await InsertPreferenceEventAsync(
                    connection,
                    transaction,
                    preference.BuildId,
                    previous?.ExtractionId,
                    preference.ExtractionId,
                    preference.SelectedAtUtc,
                    preference.SelectionReason,
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
                $"Validated extraction '{promotion.Extraction.ExtractionId}' could " +
                "not be committed atomically.",
                exception);
        }
    }

    public async Task LinkAttemptToValidatedExtractionAsync(
        ValidationPersistence validation,
        ValidatedExtraction extraction,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(validation.Attempt);
        ArgumentNullException.ThrowIfNull(validation.Report);
        ArgumentNullException.ThrowIfNull(extraction);
        if (validation.Attempt.Status != ExtractionAttemptStatus.Succeeded ||
            !string.Equals(
                validation.Attempt.ResultExtractionId,
                extraction.ExtractionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Linking an attempt to an existing validated extraction requires " +
                "the attempt to succeed with a matching result extraction ID.",
                nameof(validation));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            _ = await ReadValidatedExtractionHeaderAsync(
                connection,
                transaction,
                extraction.ExtractionId,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Validated extraction '{extraction.ExtractionId}' does not " +
                    "exist to link.");

            await InsertValidationResultAsync(
                connection,
                transaction,
                validation.Attempt,
                validation.Report,
                cancellationToken);
            await InsertValidationIssuesAsync(
                connection,
                transaction,
                validation.Report,
                cancellationToken);
            await TransitionAttemptWithinTransactionAsync(
                connection,
                transaction,
                validation.Attempt,
                expectedStatus,
                cancellationToken);

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
                $"Attempt '{validation.Attempt.AttemptId}' could not be linked to " +
                $"validated extraction '{extraction.ExtractionId}' atomically.",
                exception);
        }
    }

    public async Task SaveRevalidationAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(validation.Attempt);
        ArgumentNullException.ThrowIfNull(validation.Report);
        if (validation.Report.SubjectKind != ValidationSubjectKind.ValidatedExtraction ||
            string.IsNullOrWhiteSpace(validation.Report.SubjectExtractionId))
        {
            throw new ArgumentException(
                "A revalidation persistence command must target an existing " +
                "validated extraction.",
                nameof(validation));
        }

        if (validation.Attempt.Status != ExtractionAttemptStatus.Succeeded ||
            !string.Equals(
                validation.Attempt.ResultExtractionId,
                validation.Report.SubjectExtractionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Revalidation must transition the attempt to Succeeded with the " +
                "revalidated extraction as its result.",
                nameof(validation));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            _ = await ReadValidatedExtractionHeaderAsync(
                connection,
                transaction,
                validation.Report.SubjectExtractionId!,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Validated extraction '{validation.Report.SubjectExtractionId}' " +
                    "does not exist for revalidation.");

            await InsertValidationResultAsync(
                connection,
                transaction,
                validation.Attempt,
                validation.Report,
                cancellationToken);
            await InsertValidationIssuesAsync(
                connection,
                transaction,
                validation.Report,
                cancellationToken);
            await TransitionAttemptWithinTransactionAsync(
                connection,
                transaction,
                validation.Attempt,
                expectedStatus,
                cancellationToken);

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
                $"Revalidation for attempt '{validation.Attempt.AttemptId}' could " +
                "not be saved atomically.",
                exception);
        }
    }

    public async Task SetPreferredExtractionAsync(
        PreferredExtraction preference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentException.ThrowIfNullOrWhiteSpace(preference.BuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(preference.ExtractionId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var header = await ReadValidatedExtractionHeaderAsync(
                connection,
                transaction,
                preference.ExtractionId,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Validated extraction '{preference.ExtractionId}' does not exist.");
            if (!string.Equals(header.BuildId, preference.BuildId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Validated extraction '{preference.ExtractionId}' does not " +
                    $"belong to build '{preference.BuildId}'.");
            }

            var previous = await ReadPreferredExtractionAsync(
                connection,
                transaction,
                preference.BuildId,
                cancellationToken);
            await UpsertPreferredExtractionAsync(connection, transaction, preference, cancellationToken);
            await InsertPreferenceEventAsync(
                connection,
                transaction,
                preference.BuildId,
                previous?.ExtractionId,
                preference.ExtractionId,
                preference.SelectedAtUtc,
                preference.SelectionReason,
                cancellationToken);

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
                $"Preferred extraction for build '{preference.BuildId}' could not " +
                "be set atomically.",
                exception);
        }
    }

    public async Task ClearPreferredExtractionAsync(
        string buildId,
        string expectedExtractionId,
        ExtractionPreferenceReason reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExtractionId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM preferred_extractions
                    WHERE build_id = $buildId
                      AND extraction_id = $extractionId;
                    """;
                command.Parameters.AddWithValue("$buildId", buildId);
                command.Parameters.AddWithValue("$extractionId", expectedExtractionId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        $"Build '{buildId}' does not currently prefer extraction " +
                        $"'{expectedExtractionId}'.");
                }
            }

            await InsertPreferenceEventAsync(
                connection,
                transaction,
                buildId,
                expectedExtractionId,
                newExtractionId: null,
                occurredAtUtc,
                reason,
                cancellationToken);

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
                $"Preferred extraction for build '{buildId}' could not be cleared " +
                "atomically.",
                exception);
        }
    }

    /// <summary>
    /// Deletes a proven cleanup-eligible terminal attempt together with its
    /// attempt-scoped validation rows in one transaction. The attempt is deleted only
    /// when it is still <see cref="ExtractionAttemptStatus.Failed"/>,
    /// <see cref="ExtractionAttemptStatus.Canceled"/>, or
    /// <see cref="ExtractionAttemptStatus.Abandoned"/> with the exact expected status
    /// and completion timestamp, owns no result extraction, and is referenced by no
    /// validated extraction. Any mismatch or forced failure rolls the whole write back,
    /// so a partial delete is never visible and the caller can retry idempotently.
    /// </summary>
    public async Task DeleteCleanupEligibleAttemptAsync(
        string attemptId,
        ExtractionAttemptStatus expectedStatus,
        DateTimeOffset expectedCompletedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await GetAttemptAsync(
                connection,
                transaction,
                attemptId,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' does not exist to delete.");
            if (current.Status is not (ExtractionAttemptStatus.Failed
                or ExtractionAttemptStatus.Canceled
                or ExtractionAttemptStatus.Abandoned))
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' is not in a cleanup-eligible " +
                    $"terminal state (status {current.Status}).");
            }

            if (current.Status != expectedStatus)
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' status changed from the " +
                    $"expected {expectedStatus}.");
            }

            if (current.CompletedAtUtc != expectedCompletedAtUtc)
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' completion time changed from " +
                    "the expected value.");
            }

            if (current.ResultExtractionId is not null)
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' references a result extraction " +
                    "and cannot be deleted.");
            }

            if (await AttemptIsReferencedBySourceAsync(
                    connection,
                    transaction,
                    attemptId,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Extraction attempt '{attemptId}' is the source of a validated " +
                    "extraction and cannot be deleted.");
            }

            await DeleteAttemptScopedRowsAsync(
                connection,
                transaction,
                "extraction_validation_results",
                attemptId,
                cancellationToken);
            await DeleteAttemptScopedRowsAsync(
                connection,
                transaction,
                "extraction_validation_issues",
                attemptId,
                cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM extraction_attempts
                    WHERE attempt_id = $attemptId
                      AND status = $status
                      AND completed_at_utc = $completedAtUtc;
                    """;
                command.Parameters.AddWithValue("$attemptId", attemptId);
                command.Parameters.AddWithValue("$status", expectedStatus.ToString());
                command.Parameters.AddWithValue(
                    "$completedAtUtc",
                    FormatTimestamp(expectedCompletedAtUtc));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        $"Extraction attempt '{attemptId}' deletion affected no row.");
                }
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
                $"Extraction attempt '{attemptId}' could not be deleted atomically.",
                exception);
        }
    }

    private static async Task<bool> AttemptIsReferencedBySourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM validated_extractions
                WHERE source_attempt_id = $attemptId);
            """;
        command.Parameters.AddWithValue("$attemptId", attemptId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task DeleteAttemptScopedRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {tableName} WHERE attempt_id = $attemptId;";
        command.Parameters.AddWithValue("$attemptId", attemptId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ValidatedExtraction>> MaterializeValidatedExtractionsAsync(
        SqliteConnection connection,
        IReadOnlyList<ValidatedExtraction> headers,
        CancellationToken cancellationToken)
    {
        var materialized = new List<ValidatedExtraction>(headers.Count);
        foreach (var header in headers)
        {
            materialized.Add(await MaterializeValidatedExtractionAsync(
                connection,
                transaction: null,
                header,
                cancellationToken));
        }

        return materialized;
    }

    private static async Task<ValidatedExtraction> MaterializeValidatedExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ValidatedExtraction header,
        CancellationToken cancellationToken)
    {
        var assemblies = await ReadAssemblyStatisticsAsync(
            connection,
            transaction,
            header.ExtractionId,
            cancellationToken);
        return header with { Statistics = header.Statistics with { Assemblies = assemblies } };
    }

    private static async Task<ValidatedExtraction?> ReadValidatedExtractionHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {ValidatedExtractionColumns}
            FROM validated_extractions
            WHERE extraction_id = $extractionId;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadValidatedExtractionHeader(reader)
            : null;
    }

    private static ValidatedExtraction ReadValidatedExtractionHeader(SqliteDataReader reader) =>
        new(
            ExtractionId: reader.GetString(0),
            RecipeId: reader.GetString(1),
            BuildId: reader.GetString(2),
            ToolInstanceId: reader.GetString(3),
            SourceAttemptId: reader.GetString(4),
            ProfileId: reader.GetString(5),
            ProfileVersion: reader.GetInt32(6),
            ProfileDigest: reader.GetString(7),
            AdapterVersion: reader.GetInt32(8),
            ExtractionSchemaVersion: reader.GetInt32(9),
            ArtifactManifestDigest: reader.GetString(10),
            RootPath: reader.GetString(11),
            CreatedAtUtc: ParseStoredTimestamp(
                reader.GetString(12),
                "validated_extractions.created_at_utc"),
            TrustLevel: ParseStoredEnum<ToolTrustLevel>(
                reader.GetString(13),
                "validated_extractions.trust_level"),
            InitialValidationOutcome: ParseStoredEnum<ValidationOutcome>(
                reader.GetString(14),
                "validated_extractions.validation_outcome"),
            Statistics: new ExtractionStatistics(
                ArtifactCount: reader.GetInt32(15),
                LibraryCount: reader.GetInt32(16),
                ManagedAssemblyCount: reader.GetInt32(17),
                TypeDefinitionCount: reader.GetInt32(18),
                MethodDefinitionCount: reader.GetInt32(19),
                FieldDefinitionCount: reader.GetInt32(20),
                PropertyDefinitionCount: reader.GetInt32(21),
                EventDefinitionCount: reader.GetInt32(22),
                TotalOutputBytes: reader.GetInt64(23),
                TotalManagedBytes: reader.GetInt64(24),
                Assemblies: []));

    private static async Task<IReadOnlyList<AssemblyIdentityStatistics>> ReadAssemblyStatisticsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                assembly_name,
                COUNT(*),
                SUM(size),
                SUM(COALESCE(type_count, 0)),
                SUM(COALESCE(method_count, 0)),
                SUM(COALESCE(field_count, 0)),
                SUM(COALESCE(property_count, 0)),
                SUM(COALESCE(event_count, 0))
            FROM extraction_artifacts
            WHERE extraction_id = $extractionId
              AND assembly_name IS NOT NULL
            GROUP BY assembly_name
            ORDER BY assembly_name COLLATE NOCASE, assembly_name;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);

        var assemblies = new List<AssemblyIdentityStatistics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assemblies.Add(new AssemblyIdentityStatistics(
                AssemblyName: reader.GetString(0),
                FileCount: checked((int)reader.GetInt64(1)),
                ManagedBytes: reader.GetInt64(2),
                TypeDefinitionCount: checked((int)reader.GetInt64(3)),
                MethodDefinitionCount: checked((int)reader.GetInt64(4)),
                FieldDefinitionCount: checked((int)reader.GetInt64(5)),
                PropertyDefinitionCount: checked((int)reader.GetInt64(6)),
                EventDefinitionCount: checked((int)reader.GetInt64(7))));
        }

        return assemblies;
    }

    private static ArtifactManifestEntry ReadArtifactEntry(SqliteDataReader reader) =>
        new(
            RelativePath: reader.GetString(0),
            Kind: ParseStoredEnum<ArtifactKind>(
                reader.GetString(1),
                "extraction_artifacts.kind"),
            Size: reader.GetInt64(2),
            Sha256: reader.GetString(3),
            AssemblyName: ReadNullableString(reader, 4),
            ModuleName: ReadNullableString(reader, 5),
            TypeDefinitionCount: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            MethodDefinitionCount: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            FieldDefinitionCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            PropertyDefinitionCount: reader.IsDBNull(9) ? null : reader.GetInt32(9),
            EventDefinitionCount: reader.IsDBNull(10) ? null : reader.GetInt32(10));

    private static IReadOnlyList<ArtifactManifestEntry> SortArtifactEntries(
        IReadOnlyList<ArtifactManifestEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
            if (!seen.Add(entry.RelativePath))
            {
                throw new ArgumentException(
                    "The extraction artifacts contain an ambiguous case-insensitive " +
                    $"duplicate path '{entry.RelativePath}'.",
                    nameof(entries));
            }
        }

        return entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task InsertValidatedExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedExtraction extraction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO validated_extractions ({ValidatedExtractionColumns})
            VALUES (
                $extractionId,
                $recipeId,
                $buildId,
                $toolInstanceId,
                $sourceAttemptId,
                $profileId,
                $profileVersion,
                $profileDigest,
                $adapterVersion,
                $extractionSchemaVersion,
                $artifactManifestDigest,
                $rootPath,
                $createdAtUtc,
                $trustLevel,
                $validationOutcome,
                $artifactCount,
                $libraryCount,
                $managedAssemblyCount,
                $typeCount,
                $methodCount,
                $fieldCount,
                $propertyCount,
                $eventCount,
                $totalOutputBytes,
                $totalManagedBytes);
            """;
        command.Parameters.AddWithValue("$extractionId", extraction.ExtractionId);
        command.Parameters.AddWithValue("$recipeId", extraction.RecipeId);
        command.Parameters.AddWithValue("$buildId", extraction.BuildId);
        command.Parameters.AddWithValue("$toolInstanceId", extraction.ToolInstanceId);
        command.Parameters.AddWithValue("$sourceAttemptId", extraction.SourceAttemptId);
        command.Parameters.AddWithValue("$profileId", extraction.ProfileId);
        command.Parameters.AddWithValue("$profileVersion", extraction.ProfileVersion);
        command.Parameters.AddWithValue("$profileDigest", extraction.ProfileDigest);
        command.Parameters.AddWithValue("$adapterVersion", extraction.AdapterVersion);
        command.Parameters.AddWithValue(
            "$extractionSchemaVersion",
            extraction.ExtractionSchemaVersion);
        command.Parameters.AddWithValue(
            "$artifactManifestDigest",
            extraction.ArtifactManifestDigest);
        command.Parameters.AddWithValue("$rootPath", extraction.RootPath);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(extraction.CreatedAtUtc));
        command.Parameters.AddWithValue("$trustLevel", extraction.TrustLevel.ToString());
        command.Parameters.AddWithValue(
            "$validationOutcome",
            extraction.InitialValidationOutcome.ToString());
        command.Parameters.AddWithValue("$artifactCount", extraction.Statistics.ArtifactCount);
        command.Parameters.AddWithValue("$libraryCount", extraction.Statistics.LibraryCount);
        command.Parameters.AddWithValue(
            "$managedAssemblyCount",
            extraction.Statistics.ManagedAssemblyCount);
        command.Parameters.AddWithValue("$typeCount", extraction.Statistics.TypeDefinitionCount);
        command.Parameters.AddWithValue(
            "$methodCount",
            extraction.Statistics.MethodDefinitionCount);
        command.Parameters.AddWithValue(
            "$fieldCount",
            extraction.Statistics.FieldDefinitionCount);
        command.Parameters.AddWithValue(
            "$propertyCount",
            extraction.Statistics.PropertyDefinitionCount);
        command.Parameters.AddWithValue(
            "$eventCount",
            extraction.Statistics.EventDefinitionCount);
        command.Parameters.AddWithValue(
            "$totalOutputBytes",
            extraction.Statistics.TotalOutputBytes);
        command.Parameters.AddWithValue(
            "$totalManagedBytes",
            extraction.Statistics.TotalManagedBytes);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertExtractionArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        IReadOnlyList<ArtifactManifestEntry> orderedEntries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in orderedEntries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO extraction_artifacts (
                    extraction_id,
                    relative_path,
                    kind,
                    size,
                    sha256,
                    assembly_name,
                    module_name,
                    type_count,
                    method_count,
                    field_count,
                    property_count,
                    event_count)
                VALUES (
                    $extractionId,
                    $relativePath,
                    $kind,
                    $size,
                    $sha256,
                    $assemblyName,
                    $moduleName,
                    $typeCount,
                    $methodCount,
                    $fieldCount,
                    $propertyCount,
                    $eventCount);
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            command.Parameters.AddWithValue("$relativePath", entry.RelativePath);
            command.Parameters.AddWithValue("$kind", entry.Kind.ToString());
            command.Parameters.AddWithValue("$size", entry.Size);
            command.Parameters.AddWithValue("$sha256", entry.Sha256);
            AddNullableParameter(command, "$assemblyName", entry.AssemblyName);
            AddNullableParameter(command, "$moduleName", entry.ModuleName);
            AddNullableParameter(command, "$typeCount", entry.TypeDefinitionCount);
            AddNullableParameter(command, "$methodCount", entry.MethodDefinitionCount);
            AddNullableParameter(command, "$fieldCount", entry.FieldDefinitionCount);
            AddNullableParameter(command, "$propertyCount", entry.PropertyDefinitionCount);
            AddNullableParameter(command, "$eventCount", entry.EventDefinitionCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertValidationResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExtractionAttempt attempt,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(report.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The validation report attempt ID does not match the persisted attempt.",
                nameof(report));
        }

        var reportPath = DeriveValidationReportPath(attempt);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_validation_results (
                attempt_id,
                subject_extraction_id,
                artifact_manifest_digest,
                policy_id,
                policy_version,
                policy_digest,
                outcome,
                report_path,
                baseline_extraction_id,
                preference_eligible,
                validated_at_utc,
                artifact_count,
                library_count,
                managed_assembly_count,
                type_count,
                method_count,
                field_count,
                property_count,
                event_count,
                total_output_bytes,
                total_managed_bytes)
            VALUES (
                $attemptId,
                $subjectExtractionId,
                $artifactManifestDigest,
                $policyId,
                $policyVersion,
                $policyDigest,
                $outcome,
                $reportPath,
                $baselineExtractionId,
                $preferenceEligible,
                $validatedAtUtc,
                $artifactCount,
                $libraryCount,
                $managedAssemblyCount,
                $typeCount,
                $methodCount,
                $fieldCount,
                $propertyCount,
                $eventCount,
                $totalOutputBytes,
                $totalManagedBytes);
            """;
        command.Parameters.AddWithValue("$attemptId", report.AttemptId);
        AddNullableParameter(command, "$subjectExtractionId", report.SubjectExtractionId);
        command.Parameters.AddWithValue(
            "$artifactManifestDigest",
            report.ArtifactManifestDigest);
        command.Parameters.AddWithValue("$policyId", report.PolicyId);
        command.Parameters.AddWithValue("$policyVersion", report.PolicyVersion);
        command.Parameters.AddWithValue("$policyDigest", report.PolicyDigest);
        command.Parameters.AddWithValue("$outcome", report.Outcome.ToString());
        command.Parameters.AddWithValue("$reportPath", reportPath);
        AddNullableParameter(command, "$baselineExtractionId", report.BaselineExtractionId);
        command.Parameters.AddWithValue(
            "$preferenceEligible",
            report.PreferenceEligible ? 1 : 0);
        command.Parameters.AddWithValue(
            "$validatedAtUtc",
            FormatTimestamp(report.ValidatedAtUtc));
        command.Parameters.AddWithValue("$artifactCount", report.Statistics.ArtifactCount);
        command.Parameters.AddWithValue("$libraryCount", report.Statistics.LibraryCount);
        command.Parameters.AddWithValue(
            "$managedAssemblyCount",
            report.Statistics.ManagedAssemblyCount);
        command.Parameters.AddWithValue("$typeCount", report.Statistics.TypeDefinitionCount);
        command.Parameters.AddWithValue(
            "$methodCount",
            report.Statistics.MethodDefinitionCount);
        command.Parameters.AddWithValue("$fieldCount", report.Statistics.FieldDefinitionCount);
        command.Parameters.AddWithValue(
            "$propertyCount",
            report.Statistics.PropertyDefinitionCount);
        command.Parameters.AddWithValue("$eventCount", report.Statistics.EventDefinitionCount);
        command.Parameters.AddWithValue(
            "$totalOutputBytes",
            report.Statistics.TotalOutputBytes);
        command.Parameters.AddWithValue(
            "$totalManagedBytes",
            report.Statistics.TotalManagedBytes);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertValidationIssuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        var issues = report.Issues;
        for (var ordinal = 0; ordinal < issues.Count; ordinal++)
        {
            var issue = issues[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO extraction_validation_issues (
                    attempt_id,
                    ordinal,
                    severity,
                    code,
                    message,
                    artifact_relative_path,
                    preference_blocking)
                VALUES (
                    $attemptId,
                    $ordinal,
                    $severity,
                    $code,
                    $message,
                    $artifactRelativePath,
                    $preferenceBlocking);
                """;
            command.Parameters.AddWithValue("$attemptId", report.AttemptId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$severity", issue.Severity.ToString());
            command.Parameters.AddWithValue("$code", issue.Code);
            command.Parameters.AddWithValue("$message", issue.Message);
            AddNullableParameter(
                command,
                "$artifactRelativePath",
                issue.ArtifactRelativePath);
            command.Parameters.AddWithValue(
                "$preferenceBlocking",
                issue.PreferenceBlocking ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<PreferredExtraction?> ReadPreferredExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string buildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT build_id, extraction_id, selected_at_utc, selection_reason
            FROM preferred_extractions
            WHERE build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PreferredExtraction(
            reader.GetString(0),
            reader.GetString(1),
            ParseStoredTimestamp(
                reader.GetString(2),
                "preferred_extractions.selected_at_utc"),
            ParseStoredEnum<ExtractionPreferenceReason>(
                reader.GetString(3),
                "preferred_extractions.selection_reason"));
    }

    private static async Task UpsertPreferredExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreferredExtraction preference,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO preferred_extractions (
                build_id,
                extraction_id,
                selected_at_utc,
                selection_reason)
            VALUES (
                $buildId,
                $extractionId,
                $selectedAtUtc,
                $selectionReason)
            ON CONFLICT(build_id) DO UPDATE SET
                extraction_id = excluded.extraction_id,
                selected_at_utc = excluded.selected_at_utc,
                selection_reason = excluded.selection_reason;
            """;
        command.Parameters.AddWithValue("$buildId", preference.BuildId);
        command.Parameters.AddWithValue("$extractionId", preference.ExtractionId);
        command.Parameters.AddWithValue(
            "$selectedAtUtc",
            FormatTimestamp(preference.SelectedAtUtc));
        command.Parameters.AddWithValue(
            "$selectionReason",
            preference.SelectionReason.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPreferenceEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string buildId,
        string? previousExtractionId,
        string? newExtractionId,
        DateTimeOffset occurredAtUtc,
        ExtractionPreferenceReason reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_preference_events (
                event_id,
                build_id,
                previous_extraction_id,
                new_extraction_id,
                selected_at_utc,
                selection_reason)
            VALUES (
                $eventId,
                $buildId,
                $previousExtractionId,
                $newExtractionId,
                $selectedAtUtc,
                $selectionReason);
            """;
        command.Parameters.AddWithValue(
            "$eventId",
            Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$buildId", buildId);
        AddNullableParameter(command, "$previousExtractionId", previousExtractionId);
        AddNullableParameter(command, "$newExtractionId", newExtractionId);
        command.Parameters.AddWithValue("$selectedAtUtc", FormatTimestamp(occurredAtUtc));
        command.Parameters.AddWithValue("$selectionReason", reason.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Derives the strict validation.json path from the attempt's already-owned log
    /// paths. Storage cannot depend on S1Atlas.Extraction's OwnedAttemptPaths (the
    /// project reference runs the other way), and none of the Phase 4 persistence
    /// commands carry an explicit report path, so this mirrors the real convention:
    /// standard output/error logs live at "&lt;attempt-root&gt;/logs/...", and
    /// validation.json is a sibling of attempt.json directly under the attempt root.
    /// </summary>
    private static string DeriveValidationReportPath(ExtractionAttempt attempt)
    {
        var logsRoot = Path.GetDirectoryName(attempt.StandardOutputPath);
        var attemptRoot = string.IsNullOrEmpty(logsRoot)
            ? null
            : Path.GetDirectoryName(logsRoot);
        if (string.IsNullOrEmpty(attemptRoot))
        {
            throw new InvalidOperationException(
                $"Attempt '{attempt.AttemptId}' standard output path does not " +
                "resolve to an owned attempt root.");
        }

        return Path.Combine(attemptRoot, "validation.json");
    }

    private static void RequireLowerCaseHex(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The value must be a lower-case SHA-256 digest.",
                parameterName);
        }
    }
}
