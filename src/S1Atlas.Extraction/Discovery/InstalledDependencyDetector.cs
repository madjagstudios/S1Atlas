using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;

namespace S1Atlas.Extraction.Discovery;

public sealed class InstalledDependencyDetector : IDependencyDetector
{
    public IReadOnlyList<DependencyVersion> Detect(ScheduleOneInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var userLibsPath = Path.Combine(installation.RootPath, "UserLibs");
        var pluginsPath = Path.Combine(installation.RootPath, "Plugins");

        return
        [
            DetectDll(
                DependencyKind.S1Api,
                [userLibsPath, installation.ModsPath, pluginsPath],
                fileName => fileName.StartsWith("S1API", StringComparison.OrdinalIgnoreCase)),
            DetectDll(
                DependencyKind.S1Mapi,
                [userLibsPath, installation.ModsPath, pluginsPath],
                fileName => fileName.StartsWith("S1MAPI", StringComparison.OrdinalIgnoreCase)),
            DetectDll(
                DependencyKind.MelonLoader,
                [installation.MelonLoaderPath],
                fileName => fileName.Equals("MelonLoader.dll", StringComparison.OrdinalIgnoreCase)),
            DetectDll(
                DependencyKind.Sideload,
                [installation.ModsPath, pluginsPath, userLibsPath],
                fileName => fileName.StartsWith("Sideload", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static DependencyVersion DetectDll(
        DependencyKind kind,
        IEnumerable<string> searchRoots,
        Func<string, bool> fileNameMatches)
    {
        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var match = Directory
                .EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
                .FirstOrDefault(path => fileNameMatches(Path.GetFileName(path)));

            if (match is not null)
            {
                return new DependencyVersion(
                    kind,
                    TryReadVersion(match),
                    Path.GetFullPath(match),
                    IsInstalled: true);
            }
        }

        return new DependencyVersion(kind, Version: null, Path: null, IsInstalled: false);
    }

    private static string? TryReadVersion(string path)
    {
        try
        {
            var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }
        catch (Win32Exception)
        {
        }

        try
        {
            return AssemblyName.GetAssemblyName(path).Version?.ToString();
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }
}
