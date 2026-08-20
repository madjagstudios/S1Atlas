namespace S1Atlas.Docs.Identity;

public sealed class PortalLinkResolver
{
    public string RelativeHref(string fromPage, string toPage, string? fragment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromPage);
        ArgumentException.ThrowIfNullOrWhiteSpace(toPage);
        var from = Normalize(fromPage);
        var target = Normalize(toPage);
        var directory = from.Contains('/') ? from[..from.LastIndexOf('/')] : string.Empty;
        var relative = Path.GetRelativePath(directory.Length == 0 ? "." : directory, target)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (relative.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("Portal links must be site-relative.");
        return relative + (string.IsNullOrEmpty(fragment) ? string.Empty : "#" + fragment);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
