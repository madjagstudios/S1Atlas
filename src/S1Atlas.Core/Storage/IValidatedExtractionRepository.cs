using S1Atlas.Core.Extraction;

namespace S1Atlas.Core.Storage;

public interface IValidatedExtractionRepository
{
    Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
        string recipeId,
        CancellationToken cancellationToken);

    Task<ValidatedExtraction?> GetValidatedExtractionAsync(
        string extractionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
        string extractionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
        string? buildId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken);

    Task<StoredValidationResult?> GetLatestValidationResultAsync(
        string extractionId,
        string policyDigest,
        CancellationToken cancellationToken);

    Task<PreferredExtraction?> GetPreferredExtractionAsync(
        string buildId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
        string? buildId,
        CancellationToken cancellationToken);

    Task SaveValidationFailureAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken);

    Task CommitValidatedExtractionAsync(
        ValidatedExtractionPromotion promotion,
        CancellationToken cancellationToken);

    Task LinkAttemptToValidatedExtractionAsync(
        ValidationPersistence validation,
        ValidatedExtraction extraction,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken);

    Task SaveRevalidationAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken);

    Task SetPreferredExtractionAsync(
        PreferredExtraction preference,
        CancellationToken cancellationToken);

    Task ClearPreferredExtractionAsync(
        string buildId,
        string expectedExtractionId,
        ExtractionPreferenceReason reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task DeleteCleanupEligibleAttemptAsync(
        string attemptId,
        ExtractionAttemptStatus expectedStatus,
        DateTimeOffset expectedCompletedAtUtc,
        CancellationToken cancellationToken);
}
