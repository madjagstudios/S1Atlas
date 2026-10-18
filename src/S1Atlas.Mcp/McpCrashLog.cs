using S1Atlas.Core.Deployment;

namespace S1Atlas.Mcp;

// Last-chance handler (AT-141). Any exception nothing else caught, on any
// thread, is written to %TEMP%\s1atlas\mcp-crash-*.log so the next real
// crash leaves a stack trace. The process then exits 0 when the client went
// away and 1 otherwise, instead of the 0xE0434352 crash dialog.
internal static class McpCrashLog
{
    public static string CrashDirectory => Path.Combine(Path.GetTempPath(), "s1atlas");

    public static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var clientGone = args.ExceptionObject is Exception exception &&
            McpClientDisconnect.IsClientGone(exception);
        Write(args.ExceptionObject);
        Environment.Exit(clientGone ? 0 : 1);
    }

    private static void Write(object exceptionObject)
    {
        var path = Path.Combine(
            CrashDirectory,
            $"mcp-crash-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}.log");
        try
        {
            Directory.CreateDirectory(CrashDirectory);
            File.WriteAllText(
                path,
                $"{DateTime.UtcNow:O} s1atlas-mcp {AtlasVersion.For(typeof(McpToolCatalog).Assembly)} " +
                $"pid {Environment.ProcessId}{Environment.NewLine}{exceptionObject}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Never throw from the last-chance handler.
        }

        try
        {
            Console.Error.WriteLine($"s1atlas-mcp stopped on an uncaught error; details in {path}");
        }
        catch (Exception)
        {
            // stderr may already be closed.
        }
    }
}
