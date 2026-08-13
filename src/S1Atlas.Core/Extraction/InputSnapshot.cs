namespace S1Atlas.Core.Extraction;

public sealed record InputSnapshot(
    string InputSnapshotId,
    string BuildId,
    string RootPath,
    string ManifestDigest,
    DateTimeOffset CreatedAtUtc,
    bool ReplayVerified,
    DateTimeOffset? ReplayVerifiedAtUtc,
    InputManifest Manifest);
