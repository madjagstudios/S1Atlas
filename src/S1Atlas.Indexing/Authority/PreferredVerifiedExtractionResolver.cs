using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Authority;

public sealed class PreferredVerifiedExtractionResolver
{
    private readonly string _dataRoot;
    private readonly IValidatedExtractionRepository _repository;
    private readonly IValidatedExtractionIntegrityVerifier _integrityVerifier;

    public PreferredVerifiedExtractionResolver(
        string dataRoot,
        IValidatedExtractionRepository repository,
        IValidatedExtractionIntegrityVerifier integrityVerifier)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
    }

    public async Task<PreferredVerifiedExtraction?> ResolveAsync(
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        cancellationToken.ThrowIfCancellationRequested();

        var preference = await _repository.GetPreferredExtractionAsync(buildId, cancellationToken);
        if (preference is null || !IsUsablePreference(preference, buildId))
        {
            return null;
        }

        var extraction = await _repository.GetValidatedExtractionAsync(
            preference.ExtractionId,
            cancellationToken);
        if (extraction is null ||
            !string.Equals(extraction.ExtractionId, preference.ExtractionId, StringComparison.Ordinal) ||
            !string.Equals(extraction.BuildId, buildId, StringComparison.Ordinal))
        {
            return null;
        }

        var integrity = await _integrityVerifier.VerifyAsync(
            _dataRoot,
            buildId,
            extraction.ExtractionId,
            cancellationToken);
        if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
        {
            return null;
        }

        var finalPreference = await _repository.GetPreferredExtractionAsync(buildId, cancellationToken);
        if (finalPreference is null || !preference.Equals(finalPreference))
        {
            return null;
        }

        return new PreferredVerifiedExtraction(buildId, preference, extraction);
    }

    private static bool IsUsablePreference(PreferredExtraction? preference, string buildId) =>
        preference is not null &&
        string.Equals(preference.BuildId, buildId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(preference.ExtractionId) &&
        preference.SelectionReason is not (
            ExtractionPreferenceReason.PolicyInvalidated or
            ExtractionPreferenceReason.IntegrityInvalidated);
}
