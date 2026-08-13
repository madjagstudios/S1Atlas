namespace S1Atlas.Extraction.Attempts;

internal sealed record OwnedAttemptPaths
{
    private static readonly char[] UnsafeSegmentCharacters =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '<', '>', '"', '|', '?', '*'];

    private OwnedAttemptPaths(
        string dataRoot,
        string buildId,
        string attemptId,
        string stagingRoot,
        string attemptRoot)
    {
        DataRoot = dataRoot;
        BuildId = buildId;
        AttemptId = attemptId;
        StagingRoot = stagingRoot;
        WorkingRoot = Path.Combine(stagingRoot, "working");
        OutputRoot = Path.Combine(stagingRoot, "output");
        StagingLogsRoot = Path.Combine(stagingRoot, "logs");
        AttemptRoot = attemptRoot;
        AttemptDocumentPath = Path.Combine(attemptRoot, "attempt.json");
        FinalLogsRoot = Path.Combine(attemptRoot, "logs");
        CandidateOutputRoot = Path.Combine(attemptRoot, "candidate-output");
        RetainedOutputRoot = Path.Combine(attemptRoot, "retained-output");
    }

    public string DataRoot { get; }
    public string BuildId { get; }
    public string AttemptId { get; }
    public string StagingRoot { get; }
    public string WorkingRoot { get; }
    public string OutputRoot { get; }
    public string StagingLogsRoot { get; }
    public string AttemptRoot { get; }
    public string AttemptDocumentPath { get; }
    public string FinalLogsRoot { get; }
    public string CandidateOutputRoot { get; }
    public string RetainedOutputRoot { get; }

    public static OwnedAttemptPaths Create(
        string dataRoot,
        string buildId,
        string attemptId,
        Func<string, FileAttributes>? getFileAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        RequireSafeSegment(buildId, "build ID");
        if (!IsLowerGuidN(attemptId))
        {
            throw new ArgumentException(
                "The attempt ID must be a 32-character lower-case GUID N value.",
                nameof(attemptId));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var staging = ResolveContained(
            root, "builds", buildId, "extractions", ".staging", attemptId);
        var attempt = ResolveContained(root, "builds", buildId, "attempts", attemptId);
        var readAttributes = getFileAttributes ?? File.GetAttributes;
        EnsureSafeExistingPath(root, staging, getFileAttributes: readAttributes);
        EnsureSafeExistingPath(root, attempt, getFileAttributes: readAttributes);
        return new OwnedAttemptPaths(root, buildId, attemptId, staging, attempt);
    }

    internal static void EnsureSafeExistingPath(
        string root,
        string candidate,
        bool allowFinalFile = false,
        Func<string, FileAttributes>? getFileAttributes = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsSameOrDescendant(fullRoot, fullCandidate))
        {
            throw new InvalidOperationException("The attempt path escapes its Atlas data root.");
        }

        var current = fullRoot;
        var readAttributes = getFileAttributes ?? File.GetAttributes;
        InspectExisting(current, allowFile: false, readAttributes);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
        {
            return;
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            InspectExisting(
                current,
                allowFinalFile && index == segments.Length - 1,
                readAttributes);
        }
    }

    internal static bool IsSameOrDescendant(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLowerGuidN(string? value) =>
        value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static bool PathsEqualParent(string path, string expectedFileName) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            expectedFileName, StringComparison.Ordinal) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(path))), ".staging", StringComparison.Ordinal);

    private static string ResolveContained(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!IsSameOrDescendant(root, candidate) ||
            string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The attempt path escapes its Atlas data root.");
        }

        return candidate;
    }

    private static void RequireSafeSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(UnsafeSegmentCharacters) >= 0 ||
            value.Any(char.IsControl) || Path.GetFileName(value) != value ||
            value.EndsWith(' ') || value.EndsWith('.'))
        {
            throw new ArgumentException(
                $"The {description} is not a safe path segment.",
                nameof(value));
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
                throw new InvalidOperationException(
                    $"The attempt path crosses reparse point '{path}'.");
            }

            if (!allowFile && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(
                    $"The attempt path crosses file ancestor '{path}'.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The attempt path '{path}' could not be inspected.", exception);
        }
    }
}
