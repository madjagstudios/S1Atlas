using System.Text.Json.Serialization;

namespace S1Atlas.Cli.Output;

internal sealed record CliEnvelope<T>(
    int SchemaVersion,
    string Command,
    bool Success,
    int ExitCode,
    T? Data,
    CliError? Error);

internal sealed record CliError(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AttemptId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Stage,
    string Code,
    string Message);
