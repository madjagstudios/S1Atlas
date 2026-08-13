using System.CommandLine;
using System.Text.Json;

namespace S1Atlas.Cli.Output;

internal sealed class CommandOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _commandName;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;

    public CommandOutput(
        string commandName,
        bool json,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        _commandName = commandName;
        IsJson = json;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public bool IsJson { get; }

    public int Success<T>(T data, Action<TextWriter> writeHuman)
    {
        ArgumentNullException.ThrowIfNull(writeHuman);

        if (IsJson)
        {
            WriteJson(new CliEnvelope<T>(
                SchemaVersion: 1,
                Command: _commandName,
                Success: true,
                ExitCode: 0,
                Data: data,
                Error: null));
        }
        else
        {
            writeHuman(_standardOutput);
        }

        return 0;
    }

    public int Failure(
        int exitCode,
        string code,
        string message,
        string? attemptId = null,
        string? stage = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exitCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (IsJson)
        {
            WriteJson(new CliEnvelope<object?>(
                SchemaVersion: 1,
                Command: _commandName,
                Success: false,
                ExitCode: exitCode,
                Data: null,
                Error: new CliError(attemptId, stage, code, message)));
        }
        else
        {
            _standardError.WriteLine(message);
            if (stage is not null || attemptId is not null)
            {
                if (stage is not null)
                {
                    _standardError.WriteLine($"Stage:   {stage}");
                }

                _standardError.WriteLine($"Code:    {code}");
                if (attemptId is not null)
                {
                    _standardError.WriteLine($"Attempt: {attemptId}");
                }
            }
        }

        return exitCode;
    }

    public static Option<bool> CreateJsonOption() => new("--json")
    {
        Description = "Write one machine-readable JSON result."
    };

    private void WriteJson<T>(CliEnvelope<T> envelope)
    {
        _standardOutput.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }
}
