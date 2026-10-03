using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S1Atlas.Mcp.Tools;

// Stateless page cursors: base64url(JSON {v, h, o}). The hash binds the
// cursor to one tool, one set of effective arguments, and the resolved
// build/index, so reuse with different arguments or after a re-index fails
// instead of quietly shifting.
public static class McpPageCursor
{
    private const int Version = 1;

    public static string Encode(string hash, int offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(Version, hash, offset)));
    }

    // Decodes the cursor shape without checking the hash. Callers compare the
    // returned hash against the query they actually ran, so verification sees
    // the freshest resolved build/index instead of trusting a stale binding.
    public static bool TryDecodeShape(string? cursor, out string hash, out int offset)
    {
        hash = string.Empty;
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor))
            return false;

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(bytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is not { Version: Version, Hash.Length: > 0 } || payload.Offset < 0)
            return false;

        hash = payload.Hash;
        offset = payload.Offset;
        return true;
    }

    public static string HashFor(string tool, IEnumerable<string> arguments, string? buildId, string? indexId)
    {
        var canonical = string.Join("\n", new[] { tool }.Concat(arguments).Append(buildId ?? string.Empty).Append(indexId ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string? MintNextCursor(bool hasMore, string hash, int offset, int limit) =>
        hasMore ? Encode(hash, offset + limit) : null;

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record CursorPayload(int Version, string Hash, int Offset);
}
