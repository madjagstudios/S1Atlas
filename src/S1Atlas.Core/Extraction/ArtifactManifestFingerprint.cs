using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

/// <summary>
/// Computes the deterministic content digest of an <see cref="ArtifactManifest"/>.
/// The digest depends only on the bytes an artifact contains: its normalized
/// relative path, byte size, and SHA-256. <see cref="ArtifactManifestEntry.Kind"/>,
/// assembly/module identity, and metadata counts are annotations that describe the
/// bytes without producing them, so validator implementation changes cannot
/// duplicate byte-identical output by changing them.
/// </summary>
public static class ArtifactManifestFingerprint
{
    private const string RequiredPrefix = "reconstructed/";

    public static string Create(ArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Entries);

        var normalized = manifest.Entries
            .Select(Normalize)
            .ToArray();

        RejectCaseInsensitiveDuplicates(normalized);

        var sorted = normalized
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var writer = new CanonicalHashWriter("artifact-manifest", 1);
        writer.AppendInt32(manifest.SchemaVersion);
        writer.AppendInt32(sorted.Length);
        foreach (var entry in sorted)
        {
            writer.AppendString(entry.RelativePath);
            writer.AppendInt64(entry.Size);
            writer.AppendString(entry.Sha256);
        }

        return writer.Complete();
    }

    private static NormalizedEntry Normalize(ArtifactManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
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

        if (!path.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The path must begin with '{RequiredPrefix}'.",
                nameof(entry.RelativePath));
        }

        return new NormalizedEntry(path, entry.Size, entry.Sha256);
    }

    private static void RejectCaseInsensitiveDuplicates(IReadOnlyList<NormalizedEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.RelativePath))
            {
                throw new ArgumentException(
                    $"The manifest contains an ambiguous case-insensitive duplicate path '{entry.RelativePath}'.");
            }
        }
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

    private sealed record NormalizedEntry(string RelativePath, long Size, string Sha256);
}
