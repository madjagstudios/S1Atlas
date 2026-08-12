using System.Security;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;

namespace S1Atlas.Extraction.Discovery;

public sealed class InstalledDependencyDetector : IDependencyDetector
{
    private readonly IDependencyFileEnumerator _fileEnumerator;
    private readonly IDependencyVersionReader _versionReader;

    public InstalledDependencyDetector()
        : this(
            new SafeDependencyFileEnumerator(),
            new DependencyVersionReader())
    {
    }

    internal InstalledDependencyDetector(
        IDependencyFileEnumerator fileEnumerator,
        IDependencyVersionReader versionReader)
    {
        _fileEnumerator = fileEnumerator ??
            throw new ArgumentNullException(nameof(fileEnumerator));
        _versionReader = versionReader ??
            throw new ArgumentNullException(nameof(versionReader));
    }

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

    private DependencyVersion DetectDll(
        DependencyKind kind,
        IEnumerable<string> searchRoots,
        Func<string, bool> fileNameMatches)
    {
        foreach (var root in searchRoots)
        {
            IReadOnlyList<string> files;
            try
            {
                files = _fileEnumerator.EnumerateDlls(root);
            }
            catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
            {
                continue;
            }

            var match = files
                .Where(path => fileNameMatches(Path.GetFileName(path)))
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();

            if (match is not null)
            {
                return new DependencyVersion(
                    kind,
                    _versionReader.TryReadVersion(match),
                    match,
                    IsInstalled: true);
            }
        }

        return new DependencyVersion(kind, Version: null, Path: null, IsInstalled: false);
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}
