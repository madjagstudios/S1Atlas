namespace S1Atlas.Docs.Rendering;

// Renders installation file paths for the shareable static portal. Paths
// inside the installation root render relative to it with forward slashes;
// anything else renders as its file name with an explicit marker. Absolute
// paths and dot segments never reach the output.
public static class PortalPathDisplay
{
    private const string OutsideRootMarker = "(outside the installation root)";

    public static string? ToDisplayPath(string? path, string? installationRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parsedPath = Parse(path);
        if (parsedPath.Kind == RootKind.Relative)
        {
            return JoinOrMarker(parsedPath.Segments);
        }

        var fileName = parsedPath.Segments.Length > 0 ? parsedPath.Segments[^1] : null;
        if (string.IsNullOrWhiteSpace(installationRoot))
        {
            return WithMarker(fileName);
        }

        var parsedRoot = Parse(installationRoot);
        if (parsedRoot.Kind == parsedPath.Kind &&
            parsedRoot.Key.Equals(parsedPath.Key, parsedRoot.Comparison) &&
            IsPrefix(parsedRoot, parsedPath))
        {
            var remainder = parsedPath.Segments[parsedRoot.Segments.Length..];
            if (remainder.Length > 0)
            {
                return JoinOrMarker(remainder);
            }
        }

        return WithMarker(fileName);
    }

    private static string? JoinOrMarker(string[] segments)
    {
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return OutsideRootMarker;
        }

        return string.Join('/', segments);
    }

    private static string WithMarker(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." or "..")
        {
            return OutsideRootMarker;
        }

        return $"{fileName} {OutsideRootMarker}";
    }

    private static bool IsPrefix(ParsedPath root, ParsedPath path)
    {
        if (root.Segments.Length > path.Segments.Length)
        {
            return false;
        }

        for (var i = 0; i < root.Segments.Length; i++)
        {
            if (!root.Segments[i].Equals(path.Segments[i], root.Comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static ParsedPath Parse(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            var segments = Split(path[2..]);
            if (segments.Length < 2)
            {
                return new ParsedPath(RootKind.Invalid, path, [], StringComparison.Ordinal);
            }

            return new ParsedPath(
                RootKind.Unc,
                $"{segments[0]}/{segments[1]}",
                segments[2..],
                StringComparison.OrdinalIgnoreCase);
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return new ParsedPath(
                RootKind.Drive,
                char.ToUpperInvariant(path[0]).ToString(),
                Split(path[2..].TrimStart('/', '\\')),
                StringComparison.OrdinalIgnoreCase);
        }

        if (path.StartsWith('/'))
        {
            return new ParsedPath(RootKind.Posix, "/", Split(path.TrimStart('/')), StringComparison.Ordinal);
        }

        if (path.StartsWith('\\'))
        {
            return new ParsedPath(RootKind.Rooted, string.Empty, Split(path.TrimStart('\\')), StringComparison.OrdinalIgnoreCase);
        }

        return new ParsedPath(RootKind.Relative, string.Empty, Split(path), StringComparison.Ordinal);
    }

    private static string[] Split(string path)
    {
        return path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment.Length > 0)
            .ToArray();
    }

    private enum RootKind
    {
        Relative,
        Drive,
        Unc,
        Posix,
        Rooted,
        Invalid
    }

    private sealed record ParsedPath(RootKind Kind, string Key, string[] Segments, StringComparison Comparison);
}
