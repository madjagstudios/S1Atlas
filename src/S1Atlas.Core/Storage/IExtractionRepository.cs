using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;

namespace S1Atlas.Core.Storage;

public interface IExtractionRepository
{
    Task<GameBuild?> GetBuildAsync(
        string buildId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(
        string buildId,
        CancellationToken cancellationToken);

    Task CreateAttemptAsync(
        ExtractionAttempt attempt,
        CancellationToken cancellationToken);

    Task TransitionAttemptAsync(
        ExtractionAttempt attempt,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken);

    Task<ExtractionAttempt?> GetAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
        CancellationToken cancellationToken);

    Task SaveInputSnapshotAsync(
        InputSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<InputSnapshot?> GetInputSnapshotAsync(
        string inputSnapshotId,
        CancellationToken cancellationToken);

    Task MarkInputSnapshotReplayVerifiedAsync(
        string inputSnapshotId,
        string expectedBuildId,
        string expectedManifestDigest,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
        string buildId,
        CancellationToken cancellationToken);
}

public sealed record InstallationObservationRecord(
    string SnapshotId,
    string BuildId,
    DateTimeOffset CapturedAtUtc,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath);
