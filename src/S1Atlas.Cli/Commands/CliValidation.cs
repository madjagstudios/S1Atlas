using System.CommandLine;
using System.CommandLine.Parsing;

namespace S1Atlas.Cli.Commands;

/// <summary>
/// A parse-time option validation failure carrying the command's stable error
/// triple. Command validators throw this; InvokeCore catches it and renders
/// the same human/--json failure the action used to report.
/// </summary>
internal sealed class CliValidationException(string command, string code, string message)
    : Exception(message)
{
    public string Command { get; } = command;

    public string Code { get; } = code;
}

internal static class CliValidation
{
    public static bool HelpRequested(IReadOnlyList<string> args) =>
        Scan(args, static token => token is "-h" or "--help" or "-?" or "--version");

    public static bool JsonRequested(IReadOnlyList<string> args) =>
        Scan(args, static token => token == "--json");

    public static bool SuggestRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].StartsWith("[suggest", StringComparison.Ordinal);

    private static bool Scan(IReadOnlyList<string> args, Func<string, bool> match)
    {
        foreach (var token in args)
        {
            if (token == "--")
                return false;
            if (match(token))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads an option inside a command validator with the default applied.
    /// CommandResult.GetValue ignores DefaultValueFactory (an unset --limit
    /// reads as 0), while a mistyped value keeps an option result whose
    /// GetValue throws, so the framework still reports binding failures.
    /// </summary>
    public static T GetValue<T>(CommandResult result, Option<T> option) =>
        result.GetResult(option) is null && option.HasDefaultValue
            ? (T)option.GetDefaultValue()!
            : result.GetValue(option)!;

    public static void ClearValidators(Command command)
    {
        command.Validators.Clear();
        foreach (var subcommand in command.Subcommands)
            ClearValidators(subcommand);
    }
}
