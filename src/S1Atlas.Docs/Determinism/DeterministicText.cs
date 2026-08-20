using System.Globalization;

namespace S1Atlas.Docs.Determinism;

public sealed class DeterministicText
{
    private static readonly string[] SmallNumbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    public string FormatCount(int count) => count is >= 0 and <= 10
        ? SmallNumbers[count]
        : count.ToString(CultureInfo.InvariantCulture);

    public string FormatCoverage(int shown, int total) =>
        $"showing {FormatCount(shown)} of {FormatCount(total)}";

    public string FormatPlural(int count, string singular, string plural) =>
        (count == 1 ? "one" : FormatCount(count)) + " " + (count == 1 ? singular : plural);

    public string NormalizeLf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
