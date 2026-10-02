namespace S1Atlas.Core;

/// <summary>
/// Outcome of matching one hex-ID input against a set of stored IDs.
/// </summary>
public enum ShortIdMatchKind
{
    Resolved,
    Ambiguous,
    NotFound,
    NotAPrefix,
}

/// <summary>
/// Result of <see cref="ShortId.MatchPrefix"/>. On <see cref="ShortIdMatchKind.Ambiguous"/>,
/// <see cref="Shown"/> holds the first matches in ordinal order and
/// <see cref="TotalCount"/> holds the exact total.
/// </summary>
public sealed record ShortIdMatch(
    ShortIdMatchKind Kind,
    string? Id,
    IReadOnlyList<string> Shown,
    int TotalCount);

/// <summary>
/// Shared short-ID (hex prefix) parsing, matching, and display for every
/// build, extraction, attempt, and symbol ID selector. A short ID is 8 to 63
/// hexadecimal characters of either case, normalized to lowercase; full-length
/// IDs keep their existing exact-match path and only gain case-insensitive
/// routing through <see cref="MatchPrefix"/>.
/// </summary>
public static class ShortId
{
    public const int MinPrefixLength = 8;
    public const int MaxPrefixLength = 63;
    public const int DisplayLength = 12;
    public const int MaxShownMatches = 10;

    /// <summary>
    /// Parses a short-ID prefix: 8 to 63 hex characters of either case.
    /// Full-length IDs and non-hex input return false so callers fall through
    /// to their existing exact-match or not-found path.
    /// </summary>
    public static bool TryParsePrefix(string? input, out string prefix)
    {
        if (input is null ||
            input.Length is < MinPrefixLength or > MaxPrefixLength ||
            !IsHex(input))
        {
            prefix = string.Empty;
            return false;
        }

        prefix = input.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Parses a full-length hex ID of exactly <paramref name="length"/>
    /// characters, normalizing either case to lowercase.
    /// </summary>
    public static bool TryParseFullId(string? input, int length, out string normalized)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        if (input is null || input.Length != length || !IsHex(input))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = input.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Matches <paramref name="input"/> against stored IDs. A case-insensitive
    /// exact hit resolves even when it is also a prefix of longer IDs;
    /// otherwise a valid prefix resolves when unique, reports ambiguity with
    /// the exact total when shared, and reports <see cref="ShortIdMatchKind.NotFound"/>
    /// when unmatched. Non-prefix input reports <see cref="ShortIdMatchKind.NotAPrefix"/>
    /// so callers keep their existing not-found behavior.
    /// </summary>
    public static ShortIdMatch MatchPrefix(IEnumerable<string> ids, string input)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var exact = ids.FirstOrDefault(
            candidate => string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new ShortIdMatch(ShortIdMatchKind.Resolved, exact, [exact], 1);
        }

        if (!TryParsePrefix(input, out var prefix))
        {
            return new ShortIdMatch(ShortIdMatchKind.NotAPrefix, null, [], 0);
        }

        var matches = ids
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 0)
        {
            return new ShortIdMatch(ShortIdMatchKind.NotFound, null, [], 0);
        }

        if (matches.Length == 1)
        {
            return new ShortIdMatch(ShortIdMatchKind.Resolved, matches[0], matches, 1);
        }

        return new ShortIdMatch(
            ShortIdMatchKind.Ambiguous,
            null,
            matches.Take(MaxShownMatches).ToArray(),
            matches.Length);
    }

    /// <summary>
    /// Formats an ambiguous match's shown IDs as a comma-separated list with a
    /// "(showing M of N)" suffix when the total exceeds the shown sample.
    /// </summary>
    public static string FormatMatchList(ShortIdMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var list = string.Join(", ", match.Shown);
        return match.TotalCount > match.Shown.Count
            ? $"{list} (showing {match.Shown.Count} of {match.TotalCount})"
            : list;
    }

    /// <summary>
    /// Truncates an ID to the 12-character human display form. Shorter input
    /// passes through unchanged.
    /// </summary>
    public static string Display(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.Length > DisplayLength ? id[..DisplayLength] : id;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
