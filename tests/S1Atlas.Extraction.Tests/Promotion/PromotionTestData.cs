using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Validation;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Extraction.Tests.Promotion;

/// <summary>
/// Shared, deterministic scaffolding for the validated-extraction promotion and
/// recovery tests: a real schema-5 SQLite repository seeded with a build and tool
/// instance, a <c>Validating</c> attempt whose candidate output lives at its owned
/// candidate path, and matching artifact manifest / statistics / validation report
/// factories consistent with the integrity verifier's recomputation rules.
/// </summary>
internal static class PromotionTestData
{
    public const string BuildId = "build-a";
    public const string ToolInstanceId = "tool-instance-1";
    public const string ProfileId = "default-profile";

    public static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-13T05:00:00Z");
    public static readonly string RecipeId = new('1', 64);
    public static readonly string RecipeId2 = new('2', 64);
    public static readonly string ProfileDigest = new('a', 64);
    public static readonly string PolicyDigest = new('b', 64);

    public static async Task<SqliteAtlasRepository> CreateRepositoryAsync(
        string dataRoot, CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(
            Path.Combine(dataRoot, "atlas.db"), Path.Combine(dataRoot, "backups"));
        await repository.InitializeAsync(cancellationToken);
        await SeedBuildAsync(repository, cancellationToken);
        await SeedToolInstanceAsync(dataRoot, cancellationToken);
        return repository;
    }

    /// <summary>
    /// Creates and advances an attempt to <c>Validating</c>, writing its candidate
    /// output (a single managed <c>Assembly-CSharp.dll</c>) at the attempt's owned
    /// candidate path. Returns the attempt plus the manifest facts a promoter needs.
    /// </summary>
    public static async Task<PromotionCandidate> CreateValidatingCandidateAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        string attemptId,
        string recipeId,
        byte[] candidateBytes,
        CancellationToken cancellationToken)
    {
        var ownedCandidateRoot = OwnedValidatedExtractionPaths
            .ForAttempt(dataRoot, BuildId, attemptId).AttemptCandidatePath!;
        Directory.CreateDirectory(ownedCandidateRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(ownedCandidateRoot, "Assembly-CSharp.dll"), candidateBytes, cancellationToken);

        var (manifest, statistics) = ManagedManifest(candidateBytes);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);

        var created = CreateAttempt(attemptId, recipeId, dataRoot);
        await repository.CreateAttemptAsync(created, cancellationToken);
        var preparing = created with { Status = ExtractionAttemptStatus.Preparing, StartedAtUtc = BaseTime };
        await repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, cancellationToken);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 100 };
        await repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, cancellationToken);
        var processCompleted = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = ownedCandidateRoot
        };
        await repository.TransitionAttemptAsync(
            processCompleted, ExtractionAttemptStatus.Running, cancellationToken);
        var validating = processCompleted with { Status = ExtractionAttemptStatus.Validating };
        await repository.TransitionAttemptAsync(
            validating, ExtractionAttemptStatus.ProcessCompleted, cancellationToken);

        return new PromotionCandidate(
            validating, manifest, statistics, digest, extractionId, ownedCandidateRoot);
    }

    public static ValidationReport BuildReport(
        ExtractionAttempt attempt,
        string digest,
        ExtractionStatistics statistics,
        ValidationOutcome outcome,
        IReadOnlyList<ValidationIssue> issues,
        bool preferenceEligible,
        DateTimeOffset validatedAtUtc,
        string? subjectExtractionId = null,
        ValidationSubjectKind subjectKind = ValidationSubjectKind.CandidateOutput) => new(
        SchemaVersion: 1,
        AttemptId: attempt.AttemptId,
        SubjectKind: subjectKind,
        SubjectExtractionId: subjectExtractionId,
        BuildId: attempt.BuildId,
        RecipeId: attempt.RecipeId!,
        PolicyId: "managed-assemblies-v1",
        PolicyVersion: 1,
        PolicyDigest: PolicyDigest,
        Outcome: outcome,
        InputIntegrityPassed: true,
        ProcessIntegrityPassed: true,
        OutputContainmentPassed: outcome != ValidationOutcome.Invalid,
        ArtifactManifestDigest: digest,
        Statistics: statistics,
        BaselineExtractionId: null,
        Comparisons: [],
        Issues: issues,
        PreferenceEligible: preferenceEligible,
        ValidatedAtUtc: validatedAtUtc);

    /// <summary>
    /// Drives a real promotion up to the point where the on-disk final output is
    /// complete and recoverable but the database commit fails, leaving a complete final
    /// directory, an intact promotion journal, no database row, and a still-<c>Validating</c>
    /// source attempt: exactly the state validated-promotion recovery must register.
    /// </summary>
    public static async Task<PromotionCandidate> ProduceUnregisteredFinalAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        string attemptId,
        string recipeId,
        byte[] candidateBytes,
        IFileHasher hasher,
        ValidatedExtractionDocumentStore documentStore,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken)
    {
        var candidate = await CreateValidatingCandidateAsync(
            repository, dataRoot, attemptId, recipeId, candidateBytes, cancellationToken);
        var report = BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            preferenceEligible: true, validatedAtUtc);
        var promoter = new ValidatedExtractionPromoter(
            dataRoot,
            new ThrowingCommitRepository(repository),
            documentStore,
            new ValidatedExtractionIntegrityVerifier(documentStore, hasher, repository),
            new PromotionJournalStore(),
            new CandidateOutputInspector(hasher),
            new FixedTimeProvider(validatedAtUtc));
        try
        {
            await promoter.PromoteAsync(
                new ValidatedExtractionPromotionRequest(
                    candidate.Attempt, report, candidate.Manifest, ToolTrustLevel.ManagedPinned,
                    DeduplicationTarget: null, AutomaticPreferenceReason: null, KeepInvalidOutput: false),
                cancellationToken);
        }
        catch (ExtractionOperationException exception)
            when (exception.Code == ExtractionFailureCode.DatabasePromotionFailed)
        {
        }

        return candidate;
    }

    public static (ArtifactManifest Manifest, ExtractionStatistics Statistics) ManagedManifest(byte[] bytes)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = new ArtifactManifestEntry(
            RelativePath: "reconstructed/Assembly-CSharp.dll",
            Kind: ArtifactKind.ManagedAssembly,
            Size: bytes.Length,
            Sha256: sha256,
            AssemblyName: "Assembly-CSharp",
            ModuleName: "Assembly-CSharp.dll",
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0);
        var manifest = new ArtifactManifest(1, [entry]);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0,
            TotalOutputBytes: bytes.Length,
            TotalManagedBytes: bytes.Length,
            Assemblies: [new AssemblyIdentityStatistics("Assembly-CSharp", 1, bytes.Length, 5, 10, 2, 1, 0)]);
        return (manifest, statistics);
    }

    private static ExtractionAttempt CreateAttempt(string attemptId, string recipeId, string dataRoot)
    {
        var attemptRoot = Path.Combine(dataRoot, "builds", BuildId, "attempts", attemptId);
        return new ExtractionAttempt(
            AttemptId: attemptId,
            RecipeId: recipeId,
            BuildId: BuildId,
            ToolInstanceId: ToolInstanceId,
            ProfileId: ProfileId,
            ProfileVersion: 1,
            ProfileDigest: ProfileDigest,
            ValidationPolicyId: "managed-assemblies-v1",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: PolicyDigest,
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: ExtractionInputSource.Live,
            InputSnapshotId: null,
            Status: ExtractionAttemptStatus.Created,
            CreatedAtUtc: BaseTime,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: Path.Combine(attemptRoot, "work"),
            StandardOutputPath: Path.Combine(attemptRoot, "logs", "stdout.log"),
            StandardErrorPath: Path.Combine(attemptRoot, "logs", "stderr.log"),
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
    }

    private static Task SeedBuildAsync(
        SqliteAtlasRepository repository, CancellationToken cancellationToken) =>
        repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(BuildId, "assembly-a", "metadata-a", BaseTime, IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3", "3164500", "123", "C:\\game", "C:\\game\\GameAssembly.dll",
                    "C:\\game\\global-metadata.dat"),
                Dependencies: [],
                AtlasVersion: "0.4.0-test",
                CapturedAtUtc: BaseTime),
            cancellationToken);

    private static async Task SeedToolInstanceAsync(string dataRoot, CancellationToken cancellationToken)
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
        command.CommandText = $"""
            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                '{ToolInstanceId}', 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-13T04:00:00.0000000+00:00',
                '2026-08-13T04:30:00.0000000+00:00', 'Verified');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal sealed record PromotionCandidate(
    ExtractionAttempt Attempt,
    ArtifactManifest Manifest,
    ExtractionStatistics Statistics,
    string Digest,
    string ExtractionId,
    string OwnedCandidateRoot);

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Delegates every call to the real repository except the atomic promotion commit, which throws.</summary>
internal sealed class ThrowingCommitRepository(IValidatedExtractionRepository inner)
    : IValidatedExtractionRepository
{
    public Task CommitValidatedExtractionAsync(
        ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injected database promotion failure.");

    public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
        string recipeId, CancellationToken cancellationToken) =>
        inner.ListProcessCompletedAttemptsAsync(recipeId, cancellationToken);

    public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
        string extractionId, CancellationToken cancellationToken) =>
        inner.GetValidatedExtractionAsync(extractionId, cancellationToken);

    public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
        string extractionId, CancellationToken cancellationToken) =>
        inner.GetExtractionArtifactsAsync(extractionId, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
        string? buildId, CancellationToken cancellationToken) =>
        inner.ListValidatedExtractionsAsync(buildId, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
        string recipeId, CancellationToken cancellationToken) =>
        inner.ListValidatedExtractionsByRecipeAsync(recipeId, cancellationToken);

    public Task<StoredValidationResult?> GetLatestValidationResultAsync(
        string extractionId, string policyDigest, CancellationToken cancellationToken) =>
        inner.GetLatestValidationResultAsync(extractionId, policyDigest, cancellationToken);

    public Task<PreferredExtraction?> GetPreferredExtractionAsync(
        string buildId, CancellationToken cancellationToken) =>
        inner.GetPreferredExtractionAsync(buildId, cancellationToken);

    public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
        string? buildId, CancellationToken cancellationToken) =>
        inner.ListAttemptsAsync(buildId, cancellationToken);

    public Task SaveValidationFailureAsync(
        ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken) =>
        inner.SaveValidationFailureAsync(validation, expectedStatus, cancellationToken);

    public Task LinkAttemptToValidatedExtractionAsync(
        ValidationPersistence validation, ValidatedExtraction extraction,
        ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) =>
        inner.LinkAttemptToValidatedExtractionAsync(validation, extraction, expectedStatus, cancellationToken);

    public Task SaveRevalidationAsync(
        ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken) =>
        inner.SaveRevalidationAsync(validation, expectedStatus, cancellationToken);

    public Task SetPreferredExtractionAsync(
        PreferredExtraction preference, CancellationToken cancellationToken) =>
        inner.SetPreferredExtractionAsync(preference, cancellationToken);

    public Task ClearPreferredExtractionAsync(
        string buildId, string expectedExtractionId, ExtractionPreferenceReason reason,
        DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
        inner.ClearPreferredExtractionAsync(
            buildId, expectedExtractionId, reason, occurredAtUtc, cancellationToken);
}
