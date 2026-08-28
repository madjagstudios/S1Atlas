using System.Diagnostics.CodeAnalysis;
using S1Atlas.Core.ReferenceMods;

namespace S1Atlas.Indexing.ReferenceMods;

public sealed class ReferenceModFileSelector
{
    public IReadOnlyList<ReferenceModInputFile> Select(ReferenceModDefinition mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return Select([mod]);
    }

    public IReadOnlyList<ReferenceModInputFile> Select(IReadOnlyList<ReferenceModDefinition> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var selected = new List<ReferenceModInputFile>();
        foreach (var mod in mods)
        {
            ArgumentNullException.ThrowIfNull(mod);
            var root = Path.GetFullPath(mod.RootPath);
            if (!ReferenceModPathSafety.IsNormalDirectory(root))
            {
                throw new InvalidDataException($"Reference mod root '{mod.RootPath}' is missing or unsafe.");
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    if (ReferenceModPathSafety.ShouldSkipDirectory(root, directory))
                    {
                        continue;
                    }

                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
            if (!ReferenceModPathSafety.TryCreateInputFile(root, mod.ModId, file, out var inputFile))
            {
                continue;
            }

                    if (!Matches(mod.Include, inputFile.RelativePath) ||
                        Matches(mod.Exclude, inputFile.RelativePath))
                    {
                        continue;
                    }

                    selected.Add(inputFile with
                    {
                        DisplayName = mod.DisplayName,
                        Version = mod.Version,
                        License = mod.License
                    });
                }
            }
        }

        selected.Sort((left, right) =>
        {
            var byMod = StringComparer.Ordinal.Compare(left.ModId, right.ModId);
            return byMod != 0
                ? byMod
                : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });
        return selected;
    }

    private static bool Matches(IReadOnlyList<string> patterns, string relativePath) =>
        patterns.Any(pattern => ReferenceModGlob.IsMatch(pattern, relativePath));
}

public sealed record ReferenceModInputFile(
    string ModId,
    string FullPath,
    string RelativePath,
    ReferenceModInputKind Kind,
    string? DeclaredDocumentKind)
{
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? License { get; init; }
}

public enum ReferenceModInputKind
{
    ManagedAssembly,
    SourceText,
    TextDocument
}

internal static class ReferenceModPathSafety
{
    public static bool IsNormalDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists &&
                (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                !HasReparsePointInExistingAncestry(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool ShouldSkipDirectory(string root, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return !IsContained(root, fullPath) ||
                !Directory.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointBetween(root, fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return true;
        }
    }

    public static bool TryCreateInputFile(
        string root,
        string modId,
        string path,
        [NotNullWhen(true)] out ReferenceModInputFile? inputFile)
    {
        inputFile = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) ||
                !IsContained(root, fullPath))
            {
                throw new InvalidDataException($"Reference mod input '{path}' escaped its root.");
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                HasReparsePointBetween(root, Path.GetDirectoryName(fullPath)!))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (!TryClassify(relativePath, out var kind, out var documentKind))
            {
                return false;
            }

            inputFile = new ReferenceModInputFile(
                modId,
                fullPath,
                relativePath,
                kind,
                documentKind);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryObserveRegularFile(
        string root,
        string path,
        out ReferenceModFileObservation observation)
    {
        observation = default;
        try
        {
            if (!File.Exists(path) || !IsContained(root, path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                HasReparsePointBetween(root, Path.GetDirectoryName(path)!))
            {
                return false;
            }

            var file = new FileInfo(path);
            observation = new ReferenceModFileObservation(
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                attributes);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryClassify(
        string relativePath,
        out ReferenceModInputKind kind,
        out string? documentKind)
    {
        var extension = Path.GetExtension(relativePath);
        if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReferenceModInputKind.ManagedAssembly;
            documentKind = null;
            return true;
        }

        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReferenceModInputKind.SourceText;
            documentKind = "Source";
            return true;
        }

        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReferenceModInputKind.TextDocument;
            documentKind = ClassifyDocumentKind(relativePath);
            return true;
        }

        kind = default;
        documentKind = null;
        return false;
    }

    private static string ClassifyDocumentKind(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        return fileName.ToUpperInvariant() switch
        {
            "README" => "Readme",
            "CHANGELOG" => "Changelog",
            "DEVLOG" => "Devlog",
            _ when fileName.Contains("guide", StringComparison.OrdinalIgnoreCase) => "Guide",
            _ => "Document"
        };
    }

    public static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool HasReparsePointInExistingAncestry(DirectoryInfo? directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
        {
            current.Refresh();
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePointBetween(string root, string directoryPath)
    {
        var relative = Path.GetRelativePath(root, directoryPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal readonly record struct ReferenceModFileObservation(
    long Length,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes)
{
    public bool IsStableWith(ReferenceModFileObservation other) =>
        Length == other.Length &&
        LastWriteUtc == other.LastWriteUtc &&
        Attributes == other.Attributes;
}

internal static class ReferenceModGlob
{
    public static string Normalize(string pattern, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(pattern) ||
            Path.IsPathRooted(pattern) ||
            pattern.Contains(':') ||
            pattern.Any(char.IsControl))
        {
            throw new InvalidDataException($"Reference mod manifest field '{fieldName}' must be a contained glob pattern.");
        }

        var normalized = pattern.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) ||
                segment is "." or ".." ||
                segment.Contains("**", StringComparison.Ordinal) && !string.Equals(segment, "**", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Reference mod manifest field '{fieldName}' contains an invalid glob pattern.");
            }
        }

        return string.Join('/', segments);
    }

    public static bool IsMatch(string pattern, string relativePath)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.None);
        var pathSegments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.None);
        return IsMatch(patternSegments, 0, pathSegments, 0);
    }

    private static bool IsMatch(
        IReadOnlyList<string> patternSegments,
        int patternIndex,
        IReadOnlyList<string> pathSegments,
        int pathIndex)
    {
        if (patternIndex == patternSegments.Count)
        {
            return pathIndex == pathSegments.Count;
        }

        if (string.Equals(patternSegments[patternIndex], "**", StringComparison.Ordinal))
        {
            for (var candidate = pathIndex; candidate <= pathSegments.Count; candidate++)
            {
                if (IsMatch(patternSegments, patternIndex + 1, pathSegments, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        return pathIndex < pathSegments.Count &&
            SegmentMatches(patternSegments[patternIndex], pathSegments[pathIndex]) &&
            IsMatch(patternSegments, patternIndex + 1, pathSegments, pathIndex + 1);
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        return SegmentMatches(pattern, 0, segment, 0);
    }

    private static bool SegmentMatches(string pattern, int patternIndex, string value, int valueIndex)
    {
        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                return valueIndex == value.Length;
            }

            var token = pattern[patternIndex];
            if (token == '*')
            {
                for (var candidate = valueIndex; candidate <= value.Length; candidate++)
                {
                    if (SegmentMatches(pattern, patternIndex + 1, value, candidate))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (valueIndex == value.Length)
            {
                return false;
            }

            if (token != '?' &&
                !char.Equals(token, value[valueIndex]))
            {
                return false;
            }

            patternIndex++;
            valueIndex++;
        }
    }
}
