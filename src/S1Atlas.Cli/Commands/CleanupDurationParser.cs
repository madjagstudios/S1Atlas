using System.Globalization;

namespace S1Atlas.Cli.Commands;

internal static class CleanupDurationParser
{
    private const string ErrorMessage =
        "The cleanup duration must be a positive lower-case integer followed by " +
        "m, h, or d; maximum 36500d.";

    private static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(36500);

    /// <summary>The retention window applied when <c>--older-than</c> is omitted.</summary>
    public static TimeSpan Default => TimeSpan.FromDays(30);

    public static string DefaultText => "30d";

    public static TimeSpan Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            throw Fail();
        }

        var unit = value[^1];
        if (unit is not ('m' or 'h' or 'd'))
        {
            throw Fail();
        }

        var digits = value[..^1];
        if (digits.Length == 0 ||
            !digits.All(character => character is >= '0' and <= '9'))
        {
            throw Fail();
        }

        if (!long.TryParse(
                digits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var magnitude) ||
            magnitude <= 0)
        {
            throw Fail();
        }

        try
        {
            var duration = unit switch
            {
                'm' => TimeSpan.FromMinutes(magnitude),
                'h' => TimeSpan.FromHours(magnitude),
                _ => TimeSpan.FromDays(magnitude)
            };
            if (duration <= TimeSpan.Zero || duration > MaximumDuration)
            {
                throw Fail();
            }

            return duration;
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentOutOfRangeException)
        {
            throw Fail();
        }
    }

    private static FormatException Fail() => new(ErrorMessage);
}
