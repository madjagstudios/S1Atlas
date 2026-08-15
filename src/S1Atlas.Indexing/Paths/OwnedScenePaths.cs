namespace S1Atlas.Indexing.Paths;

public sealed record OwnedScenePaths
{
    private OwnedScenePaths(string dataRoot, string finalRoot, string stagingRoot, string completeMarkerPath, string lockPath)
    {
        DataRoot = dataRoot;
        FinalRoot = finalRoot;
        StagingRoot = stagingRoot;
        CompleteMarkerPath = completeMarkerPath;
        LockPath = lockPath;
    }

    public string DataRoot { get; }
    public string FinalRoot { get; }
    public string StagingRoot { get; }
    public string CompleteMarkerPath { get; }
    public string LockPath { get; }

    public static OwnedScenePaths ForScheduleOne(string dataRoot, string buildId, string sceneSnapshotId) =>
        ForScheduleOne(dataRoot, buildId, sceneSnapshotId, File.GetAttributes);

    internal static OwnedScenePaths ForScheduleOne(
        string dataRoot,
        string buildId,
        string sceneSnapshotId,
        Func<string, FileAttributes> getFileAttributes)
    {
        var root = NormalizeRoot(dataRoot);
        RequireLowerHex64(buildId, "build ID");
        RequireLowerHex64(sceneSnapshotId, "scene snapshot ID");
        var finalRoot = ResolveContained(root, "builds", buildId, "scene-indexes", sceneSnapshotId);
        var stagingRoot = finalRoot + ".staging";
        var lockPath = finalRoot + ".lock";
        EnsureSafeExistingPath(root, finalRoot, getFileAttributes);
        EnsureSafeExistingPath(root, stagingRoot, getFileAttributes);
        EnsureSafeExistingPath(root, Path.GetDirectoryName(lockPath)!, getFileAttributes);
        try
        {
            var attributes = getFileAttributes(lockPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The scene index lock path '{lockPath}' is a reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidOperationException($"The scene index lock path '{lockPath}' is a directory.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The scene index lock path '{lockPath}' could not be inspected.", exception);
        }

        return new OwnedScenePaths(root, finalRoot, stagingRoot, Path.Combine(finalRoot, "complete.marker"), lockPath);
    }

    private static string NormalizeRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The data root must not be empty.", nameof(dataRoot));
        return root;
    }

    private static string ResolveContained(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!IsSameOrDescendant(root, candidate) || string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The scene index path escapes its Atlas data root.");
        return candidate;
    }

    private static void EnsureSafeExistingPath(string root, string candidate, Func<string, FileAttributes> getFileAttributes)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsSameOrDescendant(fullRoot, fullCandidate))
            throw new InvalidOperationException("The scene index path escapes its Atlas data root.");

        var current = fullRoot;
        InspectExisting(current, getFileAttributes);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
            return;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            InspectExisting(current, getFileAttributes);
        }
    }

    private static void InspectExisting(string path, Func<string, FileAttributes> getFileAttributes)
    {
        try
        {
            var attributes = getFileAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The scene index path crosses reparse point '{path}'.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException($"The scene index path crosses file ancestor '{path}'.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The scene index path '{path}' could not be inspected.", exception);
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireLowerHex64(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException($"The {description} must be a 64-character lower-case hexadecimal value.", description.Replace(' ', '-'));
    }
}
