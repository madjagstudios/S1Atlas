using System.ComponentModel;
using System.Diagnostics;
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
    private readonly TimeProvider _timeProvider;

    public EnvironmentDiscoveryService(
        IScheduleOneLocator locator,
        IFileHasher fileHasher,
        IDependencyDetector dependencyDetector,
        TimeProvider? timeProvider = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        _dependencyDetector = dependencyDetector ?? throw new ArgumentNullException(nameof(dependencyDetector));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EnvironmentSnapshot?> DiscoverAsync(
        string? overridePath,
        string atlasVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasVersion);

        var installation = await _locator.LocateAsync(overridePath, cancellationToken);
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
        var buildId = BuildFingerprint.Create(gameAssemblyHash, metadataHash);
        var capturedAt = _timeProvider.GetUtcNow();

        var build = new GameBuild(
            buildId,
            TryReadGameVersion(installation.RootPath),
            SteamBuildId: null,
            gameAssemblyHash,
            metadataHash,
            capturedAt,
            IsValid: true);

        return new EnvironmentSnapshot(
            build,
            _dependencyDetector.Detect(installation),
            atlasVersion,
            capturedAt);
    }

    private static string? TryReadGameVersion(string rootPath)
    {
        var executablePath = Path.Combine(rootPath, "Schedule I.exe");
        if (!File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
