namespace S1Atlas.Indexing.Paths;

public sealed record OwnedIndexPaths
{
    private static readonly char[] UnsafeSegmentCharacters =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '<', '>', '"', '|', '?', '*'];

    private OwnedIndexPaths(
        string dataRoot,
        string codebase,
        string finalRoot,
        string stagingRoot,
        string? completeMarkerPath)
    {
        DataRoot = dataRoot;
        Codebase = codebase;
        FinalRoot = finalRoot;
        StagingRoot = stagingRoot;
        CompleteMarkerPath = completeMarkerPath;
    }

    public string DataRoot { get; }
    public string Codebase { get; }
    public string FinalRoot { get; }
    public string StagingRoot { get; }
    public string? CompleteMarkerPath { get; }

    public static OwnedIndexPaths ForScheduleOne(
        string dataRoot,
        string buildId,
        string indexId)
        => ForScheduleOne(dataRoot, buildId, indexId, File.GetAttributes);

    internal static OwnedIndexPaths ForScheduleOne(
        string dataRoot,
        string buildId,
        string indexId,
        Func<string, FileAttributes> getFileAttributes)
    {
        var root = NormalizeRoot(dataRoot);
        RequireLowerHex64(buildId, "build ID");
        RequireLowerHex64(indexId, "index ID");
        return CreateIndex(
            root,
            "schedule-i",
            ResolveContained(root, "builds", buildId, "indexes", indexId),
            getFileAttributes);
    }

    public static OwnedIndexPaths ForInstalled(
        string dataRoot,
        string codebase,
        string binarySha256,
        string indexId)
    {
        var root = NormalizeRoot(dataRoot);
        RequireCodebase(codebase);
        RequireLowerHex64(binarySha256, "binary SHA-256");
        RequireLowerHex64(indexId, "index ID");
        return CreateIndex(
            root,
            codebase,
            ResolveContained(root, "installed", codebase, binarySha256, "indexes", indexId),
            File.GetAttributes);
    }

    public static OwnedIndexPaths ForUpstream(
        string dataRoot,
        string codebase,
        string commitSha)
    {
        var root = NormalizeRoot(dataRoot);
        RequireCodebase(codebase);
        RequireLowerHex40(commitSha, "commit SHA");
        var finalRoot = ResolveContained(root, "upstream", codebase, "commits", commitSha);
        EnsureSafeExistingPath(root, finalRoot + ".staging", File.GetAttributes);
        return new OwnedIndexPaths(root, codebase, finalRoot, finalRoot + ".staging", null);
    }

    public static OwnedIndexPaths ForUpstreamIndex(
        string dataRoot,
        string codebase,
        string commitSha,
        string indexId)
    {
        var root = NormalizeRoot(dataRoot);
        RequireCodebase(codebase);
        RequireLowerHex40(commitSha, "commit SHA");
        RequireLowerHex64(indexId, "index ID");
        var commitRoot = ResolveContained(root, "upstream", codebase, "commits", commitSha);
        EnsureSafeExistingPath(root, commitRoot, File.GetAttributes);
        return CreateIndex(
            root,
            codebase,
            ResolveContained(commitRoot, "indexes", indexId),
            File.GetAttributes);
    }

    private static OwnedIndexPaths CreateIndex(
        string root,
        string codebase,
        string finalRoot,
        Func<string, FileAttributes> getFileAttributes)
    {
        var stagingRoot = finalRoot + ".staging";
        EnsureSafeExistingPath(root, finalRoot, getFileAttributes);
        EnsureSafeExistingPath(root, stagingRoot, getFileAttributes);
        return new OwnedIndexPaths(
            root,
            codebase,
            finalRoot,
            stagingRoot,
            Path.Combine(finalRoot, "complete.marker"));
    }

    private static string NormalizeRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("The data root must not be empty.", nameof(dataRoot));
        }

        return root;
    }

    private static string ResolveContained(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!IsSameOrDescendant(root, candidate) ||
            string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The index path escapes its Atlas data root.");
        }

        return candidate;
    }

    private static void EnsureSafeExistingPath(
        string root,
        string candidate,
        Func<string, FileAttributes> getFileAttributes)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsSameOrDescendant(fullRoot, fullCandidate))
        {
            throw new InvalidOperationException("The index path escapes its Atlas data root.");
        }

        var current = fullRoot;
        InspectExisting(current, allowFile: false, getFileAttributes);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
        {
            return;
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            InspectExisting(current, allowFile: false, getFileAttributes);
        }
    }

    private static void InspectExisting(
        string path,
        bool allowFile,
        Func<string, FileAttributes> getFileAttributes)
    {
        try
        {
            var attributes = getFileAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"The index path crosses reparse point '{path}'.");
            }

            if (!allowFile && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException($"The index path crosses file ancestor '{path}'.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The index path '{path}' could not be inspected.", exception);
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireCodebase(string value)
    {
        if (!string.Equals(value, "s1api", StringComparison.Ordinal) &&
            !string.Equals(value, "s1mapi", StringComparison.Ordinal))
        {
            throw new ArgumentException("The codebase must be 's1api' or 's1mapi'.", nameof(value));
        }
    }

    private static void RequireLowerHex64(string? value, string description) =>
        RequireLowerHex(value, 64, description);

    private static void RequireLowerHex40(string? value, string description) =>
        RequireLowerHex(value, 40, description);

    private static void RequireLowerHex(string? value, int length, string description)
    {
        if (value is not { Length: var actualLength } || actualLength != length ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                $"The {description} must be a {length}-character lower-case hexadecimal value.",
                description.Replace(' ', '-'));
        }
    }
}
