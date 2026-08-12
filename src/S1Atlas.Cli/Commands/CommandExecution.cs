using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class CommandExecution
{
    public static int Run(
        Func<int> action,
        CommandOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            return action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return output.Failure(
                2,
                "OperationCanceled",
                "S1Atlas operation was canceled.");
        }
        catch (Exception exception)
        {
            return output.Failure(
                1,
                "OperationalFailure",
                $"S1Atlas failed: {exception.Message}");
        }
    }
}
