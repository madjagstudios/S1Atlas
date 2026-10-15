using LibCpp2IL.Logging;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class LibCpp2IlLoggingTests
{
    [Fact]
    public void Info_WritesMessageTextToSink()
    {
        using var sink = new StringWriter();
        var writer = new TextWriterLogWriter(sink);

        writer.Info("hello info");

        Assert.Equal("hello info", sink.ToString());
    }

    [Fact]
    public void Warn_WritesMessageTextToSink()
    {
        using var sink = new StringWriter();
        var writer = new TextWriterLogWriter(sink);

        writer.Warn("hello warn");

        Assert.Equal("hello warn", sink.ToString());
    }

    [Fact]
    public void Error_WritesMessageTextToSink()
    {
        using var sink = new StringWriter();
        var writer = new TextWriterLogWriter(sink);

        writer.Error("hello error");

        Assert.Equal("hello error", sink.ToString());
    }

    [Fact]
    public void Verbose_WritesMessageTextToSink()
    {
        using var sink = new StringWriter();
        var writer = new TextWriterLogWriter(sink);

        writer.Verbose("hello verbose");

        Assert.Equal("hello verbose", sink.ToString());
    }

    [Fact]
    public void Constructor_WithNullSink_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TextWriterLogWriter(null!));
    }

    [Fact]
    public void Configure_InstallsTextWriterLogWriterOverSink()
    {
        using var sink = new StringWriter();
        try
        {
            LibCpp2IlLogging.Configure(sink, verbose: false);

            var writer = Assert.IsType<TextWriterLogWriter>(LibLogger.Writer);
            writer.Info("routed");
            Assert.Equal("routed", sink.ToString());
        }
        finally
        {
            LibLogger.Writer = new DefaultWriter();
            LibLogger.ShowVerbose = true;
        }
    }

    [Fact]
    public void Configure_WithNullSink_ThrowsAndLeavesPreviousWriterInstalled()
    {
        var previous = LibLogger.Writer;
        try
        {
            Assert.Throws<ArgumentNullException>(() => LibCpp2IlLogging.Configure(null!, verbose: false));

            Assert.Same(previous, LibLogger.Writer);
        }
        finally
        {
            LibLogger.Writer = new DefaultWriter();
            LibLogger.ShowVerbose = true;
        }
    }
}
