using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

public static class InputManifestFingerprint
{
    public static string Create(InputManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Entries);

        var entries = manifest.Entries
            .Select(Normalize)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var writer = new CanonicalHashWriter("input-manifest", 1);
        writer.AppendInt32(entries.Length);
        foreach (var entry in entries)
        {
            writer.AppendString(entry.RelativePath);
            writer.AppendString(entry.Role);
            writer.AppendInt64(entry.Size);
            writer.AppendString(entry.Sha256);
        }

        return writer.Complete();
    }

    public static string CreateSnapshotId(
        string buildId,
        string manifestDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        RequireLowerCaseSha256(manifestDigest);

        using var writer = new CanonicalHashWriter("input-snapshot", 1);
        writer.AppendString(buildId);
        writer.AppendString(manifestDigest);
        return writer.Complete();
    }

    private static NormalizedEntry Normalize(InputManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Role);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.Size);
        RequireLowerCaseSha256(entry.Sha256);

        var path = entry.RelativePath.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("The path must be relative.", nameof(entry.RelativePath));
        }

        var segments = path.Split('/');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "The path must not contain empty, dot, or dot-dot segments.",
                nameof(entry.RelativePath));
        }

        return new NormalizedEntry(path, entry.Role, entry.Size, entry.Sha256);
    }

    private static void RequireLowerCaseSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The value must be a lower-case SHA-256 digest.", nameof(value));
        }
    }

    private sealed record NormalizedEntry(string RelativePath, string Role, long Size, string Sha256);
}
