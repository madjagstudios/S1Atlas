namespace S1Atlas.Cli.Output;

internal sealed record CliEnvelope<T>(
    int SchemaVersion,
    string Command,
    bool Success,
    int ExitCode,
    T? Data,
    CliError? Error);

internal sealed record CliError(
    string Code,
    string Message);
