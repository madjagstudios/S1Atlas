namespace S1Atlas.Cli.Commands;

internal static class CliExamples
{
    internal static string With(string description, params string[] examples) =>
        description + "\nExamples:\n" + string.Join("\n", examples.Select(example => "  " + example));
}
