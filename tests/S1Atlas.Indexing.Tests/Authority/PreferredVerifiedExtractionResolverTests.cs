using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using Xunit;

namespace S1Atlas.Indexing.Tests.Authority;

public sealed class PreferredVerifiedExtractionResolverTests
{
    private const string BuildId = "build-1";
    private const string ExtractionId = "extraction-1";
    private static readonly DateTimeOffset SelectedAt =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreferredValidatedAndIntegrityPass_ReturnsVerifiedInput()
    {
        var preference = Preference(ExtractionId);
        var repository = new FakeRepository(preference, Extraction(BuildId, ExtractionId));
        var verifier = new FakeIntegrityVerifier(ValidatedExtractionIntegrityStatus.Valid);
        var resolver = CreateResolver(repository, verifier);

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(BuildId, result.BuildId);
        Assert.Equal(ExtractionId, result.Extraction.ExtractionId);
        Assert.Same(preference, result.Preference);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task PreferredPointerAlone_IsRejected()
    {
        var repository = new FakeRepository(Preference(ExtractionId), validated: null);
        var resolver = CreateResolver(repository, new FakeIntegrityVerifier(
            ValidatedExtractionIntegrityStatus.Valid));

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidatedRowThatIsNotPreferred_IsRejected()
    {
        var repository = new FakeRepository(
            Preference(ExtractionId),
            Extraction(BuildId, "different-extraction"));
        var resolver = CreateResolver(repository, new FakeIntegrityVerifier(
            ValidatedExtractionIntegrityStatus.Valid));

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task IntegrityFailure_IsRejected()
    {
        var repository = new FakeRepository(Preference(ExtractionId), Extraction(BuildId, ExtractionId));
        var verifier = new FakeIntegrityVerifier(ValidatedExtractionIntegrityStatus.Mismatch);
        var resolver = CreateResolver(repository, verifier);

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task PreferenceChangeDuringResolution_IsRejected()
    {
        var repository = new FakeRepository(
            [Preference(ExtractionId), Preference("new-extraction")],
            Extraction(BuildId, ExtractionId));
        var resolver = CreateResolver(repository, new FakeIntegrityVerifier(
            ValidatedExtractionIntegrityStatus.Valid));

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(ExtractionPreferenceReason.PolicyInvalidated)]
    [InlineData(ExtractionPreferenceReason.IntegrityInvalidated)]
    public async Task InvalidatedPreference_IsRejected(ExtractionPreferenceReason reason)
    {
        var repository = new FakeRepository(
            new PreferredExtraction(BuildId, ExtractionId, SelectedAt, reason),
            Extraction(BuildId, ExtractionId));
        var resolver = CreateResolver(repository, new FakeIntegrityVerifier(
            ValidatedExtractionIntegrityStatus.Valid));

        var result = await resolver.ResolveAsync(BuildId, CancellationToken.None);

        Assert.Null(result);
    }

    private static PreferredVerifiedExtractionResolver CreateResolver(
        FakeRepository repository,
        FakeIntegrityVerifier verifier) =>
        new("C:\\atlas", repository, verifier);

    private static PreferredExtraction Preference(string extractionId) =>
        new(BuildId, extractionId, SelectedAt, ExtractionPreferenceReason.ManualPromotion);

    private static ValidatedExtraction Extraction(string buildId, string extractionId) =>
        new(
            extractionId,
            "recipe-1",
            buildId,
            "tool-1",
            "attempt-1",
            "profile-1",
            1,
            new string('a', 64),
            1,
            1,
            new string('b', 64),
            $"C:\\atlas\\builds\\{buildId}\\extractions\\{extractionId}",
            SelectedAt,
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            new ExtractionStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []));

    private sealed class FakeIntegrityVerifier(ValidatedExtractionIntegrityStatus status)
        : IValidatedExtractionIntegrityVerifier
    {
        public int Calls { get; private set; }

        public Task<ValidatedExtractionIntegrity> VerifyAsync(
            string dataRoot,
            string buildId,
            string extractionId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ValidatedExtractionIntegrity(status, null, null));
        }
    }

    private sealed class FakeRepository : IValidatedExtractionRepository
    {
        private readonly Queue<PreferredExtraction?> _preferences;
        private readonly ValidatedExtraction? _validated;

        public FakeRepository(PreferredExtraction? preference, ValidatedExtraction? validated)
            : this([preference], validated)
        {
        }

        public FakeRepository(
            IReadOnlyList<PreferredExtraction?> preferences,
            ValidatedExtraction? validated)
        {
            _preferences = new Queue<PreferredExtraction?>(preferences);
            _validated = validated;
        }

        public Task<PreferredExtraction?> GetPreferredExtractionAsync(
            string buildId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_preferences.Count > 1 ? _preferences.Dequeue() : _preferences.Peek());

        public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
            string extractionId,
            CancellationToken cancellationToken) => Task.FromResult(_validated);

        public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
            string extractionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
            string? buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredValidationResult?> GetLatestValidationResultAsync(
            string extractionId, string policyDigest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
            string? buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveValidationFailureAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitValidatedExtractionAsync(
            ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LinkAttemptToValidatedExtractionAsync(
            ValidationPersistence validation, ValidatedExtraction extraction,
            ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveRevalidationAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetPreferredExtractionAsync(
            PreferredExtraction preference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearPreferredExtractionAsync(
            string buildId, string expectedExtractionId, ExtractionPreferenceReason reason,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteCleanupEligibleAttemptAsync(
            string attemptId, ExtractionAttemptStatus expectedStatus,
            DateTimeOffset expectedCompletedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
