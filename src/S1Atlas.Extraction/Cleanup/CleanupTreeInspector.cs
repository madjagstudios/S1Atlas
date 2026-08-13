using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// A deterministic, read-only observation of an Atlas-owned tree. The digest is an
/// observation fingerprint over sorted (entry kind, normalized relative path, size,
/// UTC last-write ticks) tuples; it never reads file bytes. Cleanup apply re-observes
/// the same tree and refuses to delete when the digest changed.
/// </summary>
internal sealed record CleanupTreeObservation(
    int FileCount,
    long ByteCount,
    DateTimeOffset NewestWriteUtc,
    string ObservationDigest);

internal enum CleanupObservationOutcome
{
    Observed,
    Missing,
    Blocked
}

internal sealed record CleanupTreeInspection(
    CleanupObservationOutcome Outcome,
    CleanupTreeObservation? Observation,
    string? BlockCode,
    string? BlockMessage)
{
    public static CleanupTreeInspection ForObservation(CleanupTreeObservation observation) =>
        new(CleanupObservationOutcome.Observed, observation, null, null);

    public static CleanupTreeInspection ForMissing() =>
        new(CleanupObservationOutcome.Missing, null, null, null);

    public static CleanupTreeInspection ForBlocked(string code, string message) =>
        new(CleanupObservationOutcome.Blocked, null, code, message);
}

/// <summary>
/// Filesystem probe used by <see cref="CleanupTreeInspector"/>. Injected so tests can
/// deterministically model reparse points, unreadable entries, and size overflow
/// without creating privileged filesystem objects.
/// </summary>
internal interface ICleanupFileSystem
{
    FileAttributes GetAttributes(string path);

    IEnumerable<string> EnumerateEntries(string path);

    long GetFileLength(string path);

    DateTimeOffset GetLastWriteUtc(string path);
}

internal sealed class SystemCleanupFileSystem : ICleanupFileSystem
{
    public static SystemCleanupFileSystem Instance { get; } = new();

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public IEnumerable<string> EnumerateEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTimeOffset GetLastWriteUtc(string path) =>
        new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
}

internal sealed class CleanupTreeInspector
{
    private const string RootRelativePath = ".";

    private readonly ICleanupFileSystem _fileSystem;

    public CleanupTreeInspector(ICleanupFileSystem? fileSystem = null) =>
        _fileSystem = fileSystem ?? SystemCleanupFileSystem.Instance;

    /// <summary>
    /// Observes <paramref name="root"/> without following any reparse point. A missing
    /// root returns an empty observation only when <paramref name="allowMissing"/> is
    /// set; otherwise it returns <see cref="CleanupObservationOutcome.Missing"/>. Any
    /// reparse point, unreadable entry, case-insensitive collision, or size overflow
    /// returns a blocked inspection instead of a partial observation.
    /// </summary>
    public CleanupTreeInspection Inspect(string root, bool allowMissing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = _fileSystem.GetAttributes(fullRoot);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return allowMissing
                ? CleanupTreeInspection.ForObservation(EmptyObservation())
                : CleanupTreeInspection.ForMissing();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Unreadable(fullRoot, exception);
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Reparse(fullRoot);
        }

        var entries = new List<Entry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if ((rootAttributes & FileAttributes.Directory) == 0)
            {
                // An owned regular-file root (for example a quarantined single file):
                // observed as a root-file entry without any traversal.
                AddEntry(entries, seenPaths, EntryKind.RootFile, RootRelativePath, fullRoot);
            }
            else
            {
                AddEntry(entries, seenPaths, EntryKind.RootDirectory, RootRelativePath, fullRoot);
                var pending = new Stack<string>();
                pending.Push(fullRoot);
                while (pending.Count > 0)
                {
                    var directory = pending.Pop();
                    foreach (var child in _fileSystem.EnumerateEntries(directory))
                    {
                        var attributes = _fileSystem.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            return Reparse(child);
                        }

                        var relative = NormalizeRelative(fullRoot, child);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            AddEntry(entries, seenPaths, EntryKind.Directory, relative, child);
                            pending.Push(child);
                        }
                        else
                        {
                            AddEntry(entries, seenPaths, EntryKind.File, relative, child);
                        }
                    }
                }
            }

            return CleanupTreeInspection.ForObservation(Fingerprint(entries));
        }
        catch (DuplicatePathException exception)
        {
            return CleanupTreeInspection.ForBlocked(
                "CleanupCaseCollision",
                exception.Message);
        }
        catch (OverflowException)
        {
            return CleanupTreeInspection.ForBlocked(
                "CleanupObservationOverflow",
                $"The cleanup tree '{fullRoot}' is too large to observe.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Unreadable(fullRoot, exception);
        }
    }

    private void AddEntry(
        List<Entry> entries,
        HashSet<string> seenPaths,
        EntryKind kind,
        string relativePath,
        string fullPath)
    {
        if (!seenPaths.Add(relativePath))
        {
            throw new DuplicatePathException(
                $"The cleanup tree contains the case-insensitive duplicate path " +
                $"'{relativePath}'.");
        }

        var isFile = kind is EntryKind.File or EntryKind.RootFile;
        var size = isFile ? _fileSystem.GetFileLength(fullPath) : 0L;
        var lastWriteUtc = _fileSystem.GetLastWriteUtc(fullPath);
        entries.Add(new Entry(kind, relativePath, size, lastWriteUtc.UtcTicks));
    }

    private static string NormalizeRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/');
    }

    private static CleanupTreeObservation Fingerprint(IReadOnlyList<Entry> entries)
    {
        var ordered = entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        var fileCount = 0;
        var byteCount = 0L;
        var newestTicks = long.MinValue;
        foreach (var entry in ordered)
        {
            builder.Append(entry.Kind switch
            {
                EntryKind.RootFile => "root-file",
                EntryKind.RootDirectory => "root-directory",
                EntryKind.File => "file",
                _ => "directory"
            });
            builder.Append('\n');
            builder.Append(entry.RelativePath);
            builder.Append('\n');
            builder.Append(entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
            builder.Append(entry.LastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');

            if (entry.Kind is EntryKind.File or EntryKind.RootFile)
            {
                fileCount = checked(fileCount + 1);
                byteCount = checked(byteCount + entry.Size);
            }

            newestTicks = Math.Max(newestTicks, entry.LastWriteTicks);
        }

        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        var newestWrite = newestTicks == long.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(newestTicks, TimeSpan.Zero);
        return new CleanupTreeObservation(fileCount, byteCount, newestWrite, digest);
    }

    private static CleanupTreeObservation EmptyObservation() =>
        Fingerprint([]);

    private static CleanupTreeInspection Reparse(string path) =>
        CleanupTreeInspection.ForBlocked(
            "CleanupReparsePoint",
            $"The cleanup tree crosses reparse point '{path}'.");

    private static CleanupTreeInspection Unreadable(string path, Exception exception) =>
        CleanupTreeInspection.ForBlocked(
            "CleanupUnreadableEntry",
            $"The cleanup tree entry '{path}' could not be inspected: {exception.Message}");

    private enum EntryKind
    {
        RootFile,
        RootDirectory,
        File,
        Directory
    }

    private sealed record Entry(
        EntryKind Kind,
        string RelativePath,
        long Size,
        long LastWriteTicks);

    private sealed class DuplicatePathException(string message) : Exception(message);
}
