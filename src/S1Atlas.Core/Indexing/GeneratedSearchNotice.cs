using System.Globalization;

namespace S1Atlas.Core.Indexing;

public static class GeneratedSearchNotice
{
    private const string HiddenSuffix = " generated result(s) hidden. Re-run with includeGenerated to include them.";

    public static string? ForHidden(int hiddenCount) =>
        hiddenCount <= 0 ? null : hiddenCount.ToString(CultureInfo.InvariantCulture) + HiddenSuffix;

    public static async Task<string?> ForHiddenAsync(Func<Task<int>> countUnfilteredAsync, int shownCount, bool includeGenerated)
    {
        if (includeGenerated)
            return null;
        var unfiltered = await countUnfilteredAsync();
        return ForHidden(unfiltered - shownCount);
    }

    public static bool TryParseHiddenCount(string? notice, out int hiddenCount)
    {
        hiddenCount = 0;
        if (string.IsNullOrEmpty(notice) || !notice.EndsWith(HiddenSuffix, StringComparison.Ordinal))
            return false;
        var digits = notice.Substring(0, notice.Length - HiddenSuffix.Length);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out hiddenCount) && hiddenCount > 0;
    }
}
