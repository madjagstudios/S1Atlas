namespace S1Atlas.Mcp;

// Exceptions that mean the stdio client went away (its pipes closed, or
// shutdown was cancelled under it). For a stdio server that is a normal
// end, not a crash. A missing file or folder is a real fault even though
// it derives from IOException.
internal static class McpClientDisconnect
{
    public static bool IsClientGone(Exception exception) => exception switch
    {
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsClientGone),
        FileNotFoundException or DirectoryNotFoundException => false,
        IOException or OperationCanceledException or ObjectDisposedException => true,
        _ => false
    };
}
