namespace S1Atlas.Indexing.Scene;

internal sealed class SceneSnapshotLock : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    private SceneSnapshotLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    public static SceneSnapshotLock Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            return new SceneSnapshotLock(
                fullPath,
                new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.SceneIndexInProgress,
                "A scene index for the same deterministic snapshot is already running.",
                exception);
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        stream.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
