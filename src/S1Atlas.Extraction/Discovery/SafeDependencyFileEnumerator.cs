using System.Security;

namespace S1Atlas.Extraction.Discovery;

internal sealed class SafeDependencyFileEnumerator : IDependencyFileEnumerator
{
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, string[]> _getFiles;
    private readonly Func<string, string[]> _getDirectories;
    private readonly Func<string, FileAttributes> _getAttributes;

    public SafeDependencyFileEnumerator()
        : this(
            Directory.Exists,
            directory => Directory.GetFiles(
                directory,
                "*.dll",
                SearchOption.TopDirectoryOnly),
            directory => Directory.GetDirectories(
                directory,
                "*",
                SearchOption.TopDirectoryOnly),
            File.GetAttributes)
    {
    }

    internal SafeDependencyFileEnumerator(
        Func<string, bool> directoryExists,
        Func<string, string[]> getFiles,
        Func<string, string[]> getDirectories,
        Func<string, FileAttributes> getAttributes)
    {
        _directoryExists = directoryExists ??
            throw new ArgumentNullException(nameof(directoryExists));
        _getFiles = getFiles ?? throw new ArgumentNullException(nameof(getFiles));
        _getDirectories = getDirectories ??
            throw new ArgumentNullException(nameof(getDirectories));
        _getAttributes = getAttributes ??
            throw new ArgumentNullException(nameof(getAttributes));
    }

    public IReadOnlyList<string> EnumerateDlls(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullRootPath = Path.GetFullPath(rootPath);
        if (!_directoryExists(fullRootPath))
        {
            return [];
        }

        var pendingDirectories = new Stack<string>();
        var discoveredFiles = new List<string>();
        pendingDirectories.Push(fullRootPath);

        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            foreach (var file in TryGetFiles(currentDirectory))
            {
                discoveredFiles.Add(Path.GetFullPath(file));
            }

            foreach (var directory in TryGetDirectories(currentDirectory))
            {
                var fullDirectoryPath = Path.GetFullPath(directory);
                if (IsReparsePointOrUnreadable(fullDirectoryPath))
                {
                    continue;
                }

                pendingDirectories.Push(fullDirectoryPath);
            }
        }

        return discoveredFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<string> TryGetFiles(string directory)
    {
        try
        {
            return _getFiles(directory) ?? [];
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return [];
        }
    }

    private IReadOnlyList<string> TryGetDirectories(string directory)
    {
        try
        {
            return _getDirectories(directory) ?? [];
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return [];
        }
    }

    private bool IsReparsePointOrUnreadable(string directory)
    {
        try
        {
            return (_getAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return true;
        }
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}
