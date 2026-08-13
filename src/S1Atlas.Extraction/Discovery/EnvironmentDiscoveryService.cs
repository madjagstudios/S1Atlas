using S1Atlas.Core.Builds;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Hashing;

namespace S1Atlas.Extraction.Discovery;

public sealed class EnvironmentDiscoveryService
{
    private readonly IScheduleOneLocator _locator;
    private readonly IFileHasher _fileHasher;
    private readonly IDependencyDetector _dependencyDetector;
    private readonly IInstallationMetadataReader _installationMetadataReader;
    private readonly TimeProvider _timeProvider;

    public EnvironmentDiscoveryService(
        IScheduleOneLocator locator,
        IFileHasher fileHasher,
        IDependencyDetector dependencyDetector,
        IInstallationMetadataReader installationMetadataReader,
        TimeProvider? timeProvider = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        _dependencyDetector = dependencyDetector ??
            throw new ArgumentNullException(nameof(dependencyDetector));
        _installationMetadataReader = installationMetadataReader ??
            throw new ArgumentNullException(nameof(installationMetadataReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EnvironmentSnapshot?> DiscoverAsync(
        string? overridePath,
        string atlasVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasVersion);

        var installation = await _locator.LocateAsync(
            overridePath,
            cancellationToken);
        if (installation is null)
        {
            return null;
        }

        var gameAssemblyHash = await _fileHasher.ComputeSha256Async(
            installation.GameAssemblyPath,
            cancellationToken);
        var metadataHash = await _fileHasher.ComputeSha256Async(
            installation.GlobalMetadataPath,
            cancellationToken);
        var capturedAt = _timeProvider.GetUtcNow();
        var observation = await _installationMetadataReader.ReadAsync(
            installation,
            cancellationToken);
        var build = new GameBuild(
            BuildFingerprint.Create(gameAssemblyHash, metadataHash),
            gameAssemblyHash,
            metadataHash,
            capturedAt,
            IsValid: true);

        return new EnvironmentSnapshot(
            IdentityVersion: 2,
            Build: build,
            Installation: observation,
            Dependencies: _dependencyDetector.Detect(installation),
            AtlasVersion: atlasVersion,
            CapturedAtUtc: capturedAt);
    }
}
