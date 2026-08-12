using System.Security;
using System.Text;

namespace S1Atlas.Extraction.Steam;

internal sealed class SteamAppManifestLocator
{
    public async Task<SteamAppManifest?> LocateAsync(
        string installationRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedInstallation = NormalizeDirectoryPath(installationRoot);
        var installationDirectory = new DirectoryInfo(normalizedInstallation);
        var commonDirectory = installationDirectory.Parent;
        var steamAppsDirectory = commonDirectory?.Parent;

        if (commonDirectory is null ||
            steamAppsDirectory is null ||
            !string.Equals(
                commonDirectory.Name,
                "common",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                steamAppsDirectory.Name,
                "steamapps",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] manifestPaths;
        try
        {
            manifestPaths = Directory
                .EnumerateFiles(
                    steamAppsDirectory.FullName,
                    "appmanifest_*.acf",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
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

        foreach (var manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var content = await ReadManifestAsync(
                    manifestPath,
                    cancellationToken);
                if (!SteamAppManifestParser.TryParse(content, out var manifest) ||
                    manifest is null ||
                    Path.IsPathRooted(manifest.InstallDirectory))
                {
                    continue;
                }

                var candidate = NormalizeDirectoryPath(Path.Combine(
                    steamAppsDirectory.FullName,
                    "common",
                    manifest.InstallDirectory));
                if (string.Equals(
                        candidate,
                        normalizedInstallation,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return manifest;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        return null;
    }

    private static async Task<string> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
