using S1Atlas.Extraction.Attempts;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// Deletes a single Atlas-owned tree bottom-up without ever following a reparse point,
/// and only when the path is strictly contained below the Atlas data root. A missing
/// path is a no-op so terminal-attempt database rows can still converge to deleted.
/// </summary>
internal sealed class CleanupFileSystemDeleter
{
    private readonly string _dataRoot;

    public CleanupFileSystemDeleter(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delete(Path.GetFullPath(path), cancellationToken);
        return Task.CompletedTask;
    }

    private void Delete(string path, CancellationToken cancellationToken)
    {
        if (!OwnedAttemptPaths.IsSameOrDescendant(_dataRoot, path) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                _dataRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to delete a path outside the Atlas data root.");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Refusing to delete through a reparse point.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(child, cancellationToken);
        }

        Directory.Delete(path, recursive: false);
    }
}
