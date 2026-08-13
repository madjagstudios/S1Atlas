using System.Globalization;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;

namespace S1Atlas.Extraction.Tools;

internal static class ToolPathPolicy
{
    private const string QuarantineTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const int GuidNLength = 32;
    private const int QuarantineTimestampLength = 19;

    private static readonly char[] UnsafeSegmentCharacters =
    [
        '/', '\\', ':', '<', '>', '"', '|', '?', '*'
    ];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string GetManagedInstallRoot(
        string toolsRoot,
        ToolDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentNullException.ThrowIfNull(definition);
        EnsureSafeSegment(definition.ToolId, "tool ID");
        EnsureSafeSegment(definition.Version, "tool version");

        return ResolveContainedRelativePath(
            toolsRoot,
            string.Join('/', definition.ToolId, definition.Version));
    }

    public static string ResolveContainedRelativePath(
        string root,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') ||
            relativePath.Any(char.IsControl))
        {
            throw Failure("The tool path must be a contained relative path.");
        }

        var normalizedRelativePath = relativePath.Replace('\\', '/');
        var segments = normalizedRelativePath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw Failure(
                "The tool path cannot contain empty, '.', or '..' segments.");
        }

        var fullRoot = NormalizeRoot(root);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            Path.Combine(segments)));
        if (!IsSameOrDescendant(fullRoot, candidate) ||
            string.Equals(fullRoot, candidate, PathComparison))
        {
            throw Failure("The tool path escapes its Atlas-owned root.");
        }

        return candidate;
    }

    public static void EnsureNoReparsePointInExistingPath(
        string root,
        string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var fullRoot = NormalizeRoot(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsSameOrDescendant(fullRoot, fullCandidate))
        {
            throw Failure("The tool path escapes its Atlas-owned root.");
        }

        CheckExistingPath(fullRoot);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
        {
            return;
        }

        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckExistingPath(current);
        }
    }

    public static string CreateStagingPath(
        string stagingRoot,
        ToolDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(definition);
        EnsureSafeSegment(definition.ToolId, "tool ID");
        EnsureSafeSegment(definition.Version, "tool version");

        return ResolveContainedRelativePath(
            stagingRoot,
            $"{definition.ToolId}-{definition.Version}-{Guid.NewGuid():N}");
    }

    public static string CreateQuarantinePath(
        string quarantineRoot,
        ToolDefinition definition,
        DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        ArgumentNullException.ThrowIfNull(definition);
        EnsureSafeSegment(definition.ToolId, "tool ID");
        EnsureSafeSegment(definition.Version, "tool version");

        var timestampText = timestamp
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return ResolveContainedRelativePath(
            quarantineRoot,
            $"{definition.ToolId}-{definition.Version}-{timestampText}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Recognizes an Atlas-owned tool staging child name of the exact shape
    /// <c>&lt;safe-prefix&gt;-&lt;32 lower-hex GUID N&gt;</c>. The prefix (tool ID plus
    /// version) may itself contain hyphens, so the GUID is matched from the fixed
    /// suffix rather than by splitting on '-'.
    /// </summary>
    public static bool IsOwnedToolStagingEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            Path.GetFileName(name) != name ||
            name.Length <= GuidNLength + 1)
        {
            return false;
        }

        var separatorIndex = name.Length - GuidNLength - 1;
        if (name[separatorIndex] != '-')
        {
            return false;
        }

        var guid = name[(separatorIndex + 1)..];
        var prefix = name[..separatorIndex];
        return OwnedAttemptPaths.IsLowerGuidN(guid) && IsSafePrefix(prefix);
    }

    /// <summary>
    /// Recognizes an Atlas-owned tool quarantine child name of the exact shape
    /// <c>&lt;safe-prefix&gt;-&lt;yyyyMMddTHHmmssfffZ&gt;-&lt;32 lower-hex GUID N&gt;</c>
    /// and returns the embedded UTC timestamp. Parsing works from the fixed-width
    /// suffix because the prefix may contain hyphens.
    /// </summary>
    public static bool TryGetQuarantineTimestampUtc(
        string name,
        out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        const int suffixLength = QuarantineTimestampLength + GuidNLength + 2;
        if (string.IsNullOrEmpty(name) ||
            Path.GetFileName(name) != name ||
            name.Length <= suffixLength)
        {
            return false;
        }

        var guidSeparatorIndex = name.Length - GuidNLength - 1;
        var timestampSeparatorIndex = guidSeparatorIndex - QuarantineTimestampLength - 1;
        if (name[guidSeparatorIndex] != '-' || name[timestampSeparatorIndex] != '-')
        {
            return false;
        }

        var guid = name[(guidSeparatorIndex + 1)..];
        var timestampText = name[(timestampSeparatorIndex + 1)..guidSeparatorIndex];
        var prefix = name[..timestampSeparatorIndex];
        if (!OwnedAttemptPaths.IsLowerGuidN(guid) || !IsSafePrefix(prefix))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                timestampText,
                QuarantineTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        timestampUtc = parsed;
        return true;
    }

    private static bool IsSafePrefix(string prefix) =>
        !string.IsNullOrEmpty(prefix) &&
        prefix.IndexOfAny(UnsafeSegmentCharacters) < 0 &&
        !prefix.Any(char.IsControl);

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static void EnsureSafeSegment(string segment, string description)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." ||
            segment.IndexOfAny(UnsafeSegmentCharacters) >= 0 ||
            segment.Any(char.IsControl) ||
            Path.GetFileName(segment) != segment)
        {
            throw Failure($"The {description} is not a safe path segment.");
        }
    }

    private static void CheckExistingPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    $"The tool path crosses reparse point '{path}'.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure(
                $"The tool path '{path}' could not be inspected: {exception.Message}",
                exception);
        }
        catch (IOException exception)
        {
            throw Failure(
                $"The tool path '{path}' could not be inspected: {exception.Message}",
                exception);
        }
    }

    private static ToolOperationException Failure(
        string message,
        Exception? innerException = null) =>
        new("ToolInstallationFailed", message, innerException);
}
