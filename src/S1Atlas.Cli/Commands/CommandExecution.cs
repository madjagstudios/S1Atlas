namespace S1Atlas.Cli.Commands;

internal static class CommandExecution
{
    public static int Run(
        Func<int> action,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("S1Atlas operation was canceled.");
            return 2;
        }
        catch (Exception exception)
        {
            error.WriteLine($"S1Atlas failed: {exception.Message}");
            return 1;
        }
    }
}
