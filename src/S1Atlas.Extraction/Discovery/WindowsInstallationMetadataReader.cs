using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Extraction.Steam;

namespace S1Atlas.Extraction.Discovery;

public sealed class WindowsInstallationMetadataReader : IInstallationMetadataReader
{
    private readonly SteamAppManifestLocator _steamLocator;
    private readonly Func<string, string?> _executableVersionProbe;

    public WindowsInstallationMetadataReader()
        : this(
            new SteamAppManifestLocator(),
            path => FileVersionInfo.GetVersionInfo(path).FileVersion)
    {
    }

    internal WindowsInstallationMetadataReader(
        SteamAppManifestLocator steamLocator,
        Func<string, string?> executableVersionProbe)
    {
        _steamLocator = steamLocator ??
            throw new ArgumentNullException(nameof(steamLocator));
        _executableVersionProbe = executableVersionProbe ??
            throw new ArgumentNullException(nameof(executableVersionProbe));
    }

    public async Task<InstallationObservation> ReadAsync(
        ScheduleOneInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();

        var installationRoot = Path.GetFullPath(installation.RootPath);
        var executablePath = Path.GetFullPath(installation.ExecutablePath);
        var gameAssemblyPath = Path.GetFullPath(installation.GameAssemblyPath);
        var globalMetadataPath = Path.GetFullPath(installation.GlobalMetadataPath);
        var executableVersion = TryReadExecutableVersion(executablePath);
        var steamManifest = await _steamLocator.LocateAsync(
            installationRoot,
            cancellationToken);

        return new InstallationObservation(
            ExecutableVersion: executableVersion,
            SteamAppId: steamManifest?.AppId,
            SteamBuildId: steamManifest?.BuildId,
            InstallationRoot: installationRoot,
            GameAssemblyPath: gameAssemblyPath,
            GlobalMetadataPath: globalMetadataPath);
    }

    private string? TryReadExecutableVersion(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            return _executableVersionProbe(executablePath);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }
}
