using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Storage.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.TestSupport.Seeding;

public sealed record SeededExtraction(
    ValidatedExtraction Extraction,
    ValidationReport Report,
    InputSnapshot InputSnapshot);

// Seeds a healthy validated extraction through the real repository path:
// tool instance, input snapshot, attempt lifecycle, final documents, and the
// validated-extraction promotion. Fixture callers pass their own identifiers
// and digests; the fixed tool-row values below keep existing assertions
// stable.
public static class ExtractionSeed
{
    public static async Task SeedToolInstanceAsync(
        string dataRoot,
        string toolInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataRoot, "atlas.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                $toolInstanceId, 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-16T00:00:00.0000000+00:00',
                '2026-08-16T00:05:00.0000000+00:00', 'Verified');
            """;
        command.Parameters.AddWithValue("$toolInstanceId", toolInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static EnvironmentSnapshot CreateSnapshot(
        string buildId,
        DateTimeOffset baseTime,
        string? installationRoot = null,
        IReadOnlyList<DependencyVersion>? dependencies = null)
    {
        var root = installationRoot ?? $"C:\\game\\{buildId}";
        return new(
            IdentityVersion: 2,
            Build: new GameBuild(
                buildId,
                "assembly-" + buildId,
                "metadata-" + buildId,
                baseTime,
                IsValid: true),
            Installation: new InstallationObservation(
                "2022.3",
                "3164500",
                buildId,
                root,
                $"{root}\\GameAssembly.dll",
                $"{root}\\global-metadata.dat"),
            Dependencies: dependencies ?? [],
            AtlasVersion: "0.2.0-test",
            CapturedAtUtc: baseTime);
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static async Task<SeededExtraction> SeedValidatedExtractionAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        string buildId,
        string recipeId,
        string toolInstanceId,
        string profileDigest,
        string policyDigest,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        var manifest = CreateManifest();
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var inputSnapshot = InputSnapshot.CreateUnverified(
            buildId,
            Path.Combine(dataRoot, "inputs"),
            new InputManifest([]),
            baseTime);
        await repository.SaveInputSnapshotAsync(inputSnapshot, cancellationToken);
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            inputSnapshot.InputSnapshotId,
            buildId,
            inputSnapshot.ManifestDigest,
            baseTime.AddMinutes(1),
            cancellationToken);
        var attempt = await AdvanceAttemptToValidatingAsync(
            repository,
            buildId,
            recipeId,
            extractionId[..32],
            inputSnapshot.InputSnapshotId,
            toolInstanceId,
            profileDigest,
            policyDigest,
            baseTime,
            cancellationToken);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0,
            TotalOutputBytes: 6,
            TotalManagedBytes: 6,
            Assemblies:
            [
                new AssemblyIdentityStatistics(
                    "Assembly-CSharp",
                    1,
                    6,
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
        var extraction = new ValidatedExtraction(
            extractionId,
            recipeId,
            buildId,
            toolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            profileDigest,
            1,
            1,
            digest,
            GetFinalExtractionRoot(dataRoot, buildId, extractionId),
            baseTime.AddMinutes(10),
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            statistics);
        var report = new ValidationReport(
            1,
            attempt.AttemptId,
            ValidationSubjectKind.CandidateOutput,
            null,
            buildId,
            recipeId,
            "managed-assemblies-v1",
            1,
            policyDigest,
            ValidationOutcome.Valid,
            true,
            true,
            true,
            digest,
            statistics,
            null,
            [],
            [],
            true,
            baseTime.AddMinutes(11));
        var promotion = new ValidatedExtractionPromotion(
            attempt with
            {
                Status = ExtractionAttemptStatus.Succeeded,
                CompletedAtUtc = baseTime.AddMinutes(11),
                ResultExtractionId = extractionId
            },
            extraction,
            manifest,
            report,
            AutomaticPreferenceReason: null);

        await WriteFinalDocumentsAsync(dataRoot, extraction, manifest, report, cancellationToken);
        await repository.CommitValidatedExtractionAsync(promotion, cancellationToken);

        return new SeededExtraction(extraction, report, inputSnapshot);
    }

    // Advances a fresh attempt to Validating without promoting it, for
    // fixtures that need a live candidate alongside the healthy seed.
    public static async Task<ExtractionAttempt> AdvanceAttemptToValidatingAsync(
        SqliteAtlasRepository repository,
        string buildId,
        string recipeId,
        string attemptId,
        string inputSnapshotId,
        string toolInstanceId,
        string profileDigest,
        string policyDigest,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        var created = new ExtractionAttempt(
            AttemptId: attemptId,
            RecipeId: recipeId,
            BuildId: buildId,
            ToolInstanceId: toolInstanceId,
            ProfileId: "default-profile",
            ProfileVersion: 1,
            ProfileDigest: profileDigest,
            ValidationPolicyId: "managed-assemblies-v1",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: policyDigest,
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: ExtractionInputSource.Live,
            InputSnapshotId: inputSnapshotId,
            Status: ExtractionAttemptStatus.Created,
            CreatedAtUtc: baseTime,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: $"C:\\attempts\\{attemptId}\\work",
            StandardOutputPath: $"C:\\attempts\\{attemptId}\\logs\\stdout.log",
            StandardErrorPath: $"C:\\attempts\\{attemptId}\\logs\\stderr.log",
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
        await repository.CreateAttemptAsync(created, cancellationToken);

        var preparing = created with
        {
            Status = ExtractionAttemptStatus.Preparing,
            StartedAtUtc = baseTime
        };
        await repository.TransitionAttemptAsync(
            preparing,
            ExtractionAttemptStatus.Created,
            cancellationToken);

        var running = preparing with
        {
            Status = ExtractionAttemptStatus.Running,
            ProcessId = 1234
        };
        await repository.TransitionAttemptAsync(
            running,
            ExtractionAttemptStatus.Preparing,
            cancellationToken);

        var processCompleted = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = $"C:\\attempts\\{attemptId}\\candidate-output"
        };
        await repository.TransitionAttemptAsync(
            processCompleted,
            ExtractionAttemptStatus.Running,
            cancellationToken);

        var validating = processCompleted with
        {
            Status = ExtractionAttemptStatus.Validating
        };
        await repository.TransitionAttemptAsync(
            validating,
            ExtractionAttemptStatus.ProcessCompleted,
            cancellationToken);
        return validating;
    }

    private static async Task WriteFinalDocumentsAsync(
        string dataRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        var documentsRoot = GetFinalExtractionRoot(
            dataRoot,
            extraction.BuildId,
            extraction.ExtractionId);
        var reconstructedRoot = Path.Combine(documentsRoot, "reconstructed");
        Directory.CreateDirectory(reconstructedRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(reconstructedRoot, "Assembly-CSharp.dll"),
            [10, 20, 30, 40, 50, 60],
            cancellationToken);

        await WriteValidatedExtractionDocumentsAsync(
            dataRoot,
            documentsRoot,
            extraction,
            manifest,
            report,
            cancellationToken);
    }

    private static ArtifactManifest CreateManifest()
    {
        var sha = Convert.ToHexString(
            SHA256.HashData([10, 20, 30, 40, 50, 60]))
            .ToLowerInvariant();
        return new ArtifactManifest(
            1,
            [
                new ArtifactManifestEntry(
                    "reconstructed/Assembly-CSharp.dll",
                    ArtifactKind.ManagedAssembly,
                    6,
                    sha,
                    "Assembly-CSharp",
                    "Assembly-CSharp.dll",
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
    }

    private static string GetFinalExtractionRoot(string dataRoot, string buildId, string extractionId) =>
        Path.Combine(dataRoot, "builds", buildId, "extractions", extractionId);

    private static async Task WriteValidatedExtractionDocumentsAsync(
        string dataRoot,
        string documentsRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        var extractionAssembly = typeof(ValidatedExtractionIntegrityVerifier).Assembly;
        var storeType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionDocumentStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(storeType)
            ?? throw new InvalidOperationException("Could not create validated extraction document store.");
        var writeMethod = storeType.GetMethod("WriteFinalDocumentsAsync")
            ?? throw new InvalidOperationException("Validated extraction document writer was not found.");

        var writeTask = (Task)writeMethod.Invoke(
            store,
            [dataRoot, documentsRoot, extraction, manifest, report, cancellationToken])!;
        await writeTask;
    }
}
