namespace S1Atlas.Core.Extraction;

public sealed record InputSnapshot(
    string InputSnapshotId,
    string BuildId,
    string RootPath,
    string ManifestDigest,
    DateTimeOffset CreatedAtUtc,
    bool ReplayVerified,
    DateTimeOffset? ReplayVerifiedAtUtc,
    InputManifest Manifest)
{
    public static InputSnapshot CreateUnverified(
        string buildId,
        string inputsRoot,
        InputManifest manifest,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputsRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        var manifestDigest = InputManifestFingerprint.Create(manifest);
        var inputSnapshotId = InputManifestFingerprint.CreateSnapshotId(
            buildId,
            manifestDigest);
        return new InputSnapshot(
            inputSnapshotId,
            buildId,
            Path.Combine(Path.GetFullPath(inputsRoot), inputSnapshotId),
            manifestDigest,
            createdAtUtc.ToUniversalTime(),
            ReplayVerified: false,
            ReplayVerifiedAtUtc: null,
            manifest);
    }
}
