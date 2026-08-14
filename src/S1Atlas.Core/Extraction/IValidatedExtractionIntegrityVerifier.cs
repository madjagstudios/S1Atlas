namespace S1Atlas.Core.Extraction;

public interface IValidatedExtractionIntegrityVerifier
{
    Task<ValidatedExtractionIntegrity> VerifyAsync(
        string dataRoot,
        string buildId,
        string extractionId,
        CancellationToken cancellationToken);
}
