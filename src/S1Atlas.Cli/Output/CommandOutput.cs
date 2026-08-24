using System.CommandLine;
using System.Text.Json;
using S1Atlas.Cli.Performance;

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

    /// <summary>
    /// Writes a successful-shaped data result but with an explicit exit code. Used when
    /// an operation produced valid data yet must still exit non-zero (for example a
    /// cleanup apply that left blocked or failed items). The JSON envelope's exit code
    /// matches the returned process exit code.
    /// </summary>
    public int Complete<T>(int exitCode, T data, Action<TextWriter> writeHuman)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exitCode);
        ArgumentNullException.ThrowIfNull(writeHuman);

        if (IsJson)
        {
            WriteJson(new CliEnvelope<T>(
                SchemaVersion: 1,
                Command: _commandName,
                Success: exitCode == 0,
                ExitCode: exitCode,
                Data: data,
                Error: null));
        }
        else
        {
            writeHuman(_standardOutput);
        }

        return exitCode;
    }

    public int Failure(
        int exitCode,
        string code,
        string message,
        string? attemptId = null,
        string? stage = null) =>
        Failure<object?>(exitCode, code, message, null, attemptId, stage);

    public int Failure<T>(
        int exitCode,
        string code,
        string message,
        T data,
        string? attemptId = null,
        string? stage = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exitCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (IsJson)
        {
            WriteJson(new CliEnvelope<T>(
                SchemaVersion: 1,
                Command: _commandName,
                Success: false,
                ExitCode: exitCode,
                Data: data,
                Error: new CliError(attemptId, stage, code, message)));
        }
        else
        {
            _standardError.WriteLine(message);
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

        return exitCode;
    }

    public int FailureWithData<T>(string code, string message, T data, Action<TextWriter> writeHuman)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(message); ArgumentNullException.ThrowIfNull(writeHuman);
        if (IsJson)
        {
            WriteJson(new CliEnvelope<T>(1, _commandName, false, 1, data, new CliError(null, null, code, message)));
        }
        else
        {
            writeHuman(_standardOutput);
            _standardError.WriteLine(message);
            _standardError.WriteLine($"Code:    {code}");
        }
        return 1;
    }

    public static Option<bool> CreateJsonOption() => new("--json")
    {
        Description = "Write one machine-readable JSON result."
    };

    public void WritePerformanceReport(PerformanceMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        _standardError.WriteLine(measurement.ToJson(measurement.Complete()));
    }

    private void WriteJson<T>(CliEnvelope<T> envelope)
    {
        _standardOutput.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }
}
