using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Docs.Identity;

public sealed record PortalSlugResult(
    string ReadableSlug,
    string HashSuffix,
    string HashPrefix,
    string FileStem);

public sealed class PortalSlugService
{
    private const int ReadableLength = 80;

    public PortalSlugResult Create(string exactKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactKey);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactKey))).ToLowerInvariant();
        var readable = Readable(exactKey);
        return new PortalSlugResult(readable, hash[..12], hash[..2], readable + "-" + hash[..12]);
    }

    public string MemberAnchor(string exactCanonicalKey)
    {
        var slug = Create(exactCanonicalKey);
        return "member-" + slug.HashSuffix;
    }

    private static string Readable(string exactKey)
    {
        var normalized = exactKey.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var pendingHyphen = false;
        foreach (var character in normalized)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0) builder.Append('-');
                pendingHyphen = false;
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0)
            {
                pendingHyphen = true;
            }
        }
        var readable = builder.ToString().Trim('-');
        if (readable.Length > ReadableLength) readable = readable[..ReadableLength].TrimEnd('-');
        if (string.IsNullOrEmpty(readable) || IsWindowsDeviceName(readable)) readable = "x-" + readable;
        return readable;
    }

    private static bool IsWindowsDeviceName(string value)
    {
        var baseName = value.Split('.')[0];
        return baseName is "con" or "prn" or "aux" or "nul" ||
               (baseName.Length == 4 && (baseName.StartsWith("com", StringComparison.Ordinal) || baseName.StartsWith("lpt", StringComparison.Ordinal)) && baseName[3] is >= '1' and <= '9');
    }
}
