using LibCpp2IL.Logging;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// A LibCpp2IL <see cref="LogWriter"/> that routes log messages to a supplied <see cref="TextWriter"/> sink,
/// so library progress output can be kept off stdout (for example, by pointing it at stderr).
/// </summary>
public sealed class TextWriterLogWriter : LogWriter
{
    private readonly TextWriter _sink;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextWriterLogWriter"/> class.
    /// </summary>
    /// <param name="sink">The writer receiving LibCpp2IL log messages.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sink"/> is null.</exception>
    public TextWriterLogWriter(TextWriter sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// Writes an informational message to the configured sink.
    /// </summary>
    /// <param name="message">The message text to write.</param>
    public override void Info(string message) => _sink.Write(message);

    /// <summary>
    /// Writes a warning message to the configured sink.
    /// </summary>
    /// <param name="message">The message text to write.</param>
    public override void Warn(string message) => _sink.Write(message);

    /// <summary>
    /// Writes an error message to the configured sink.
    /// </summary>
    /// <param name="message">The message text to write.</param>
    public override void Error(string message) => _sink.Write(message);

    /// <summary>
    /// Writes a verbose message to the configured sink.
    /// </summary>
    /// <param name="message">The message text to write.</param>
    public override void Verbose(string message) => _sink.Write(message);
}

/// <summary>
/// Configures LibCpp2IL's process-global logging to route through S1Atlas-owned sinks.
/// </summary>
public static class LibCpp2IlLogging
{
    /// <summary>
    /// Installs a <see cref="TextWriterLogWriter"/> over <paramref name="sink"/> as the LibCpp2IL
    /// log writer and sets whether verbose messages are emitted.
    /// </summary>
    /// <param name="sink">The writer receiving LibCpp2IL log messages.</param>
    /// <param name="verbose">Whether LibCpp2IL verbose messages are emitted.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sink"/> is null.</exception>
    public static void Configure(TextWriter sink, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(sink);

        LibLogger.Writer = new TextWriterLogWriter(sink);
        LibLogger.ShowVerbose = verbose;
    }
}
