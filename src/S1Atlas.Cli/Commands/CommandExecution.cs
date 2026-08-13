using S1Atlas.Cli.Output;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

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
        catch (ExtractionOperationException exception)
        {
            var exitCode = exception.Code == ExtractionFailureCode.OperationCanceled
                ? 2
                : 1;
            var message = exception.Code == ExtractionFailureCode.BuildInputMismatch
                ? $"{exception.Message} Run 's1atlas scan' to index the current game bytes."
                : exception.Message;
            return output.Failure(
                exitCode,
                exception.Code.ToString(),
                message,
                exception.AttemptId,
                exception.Stage.ToString());
        }
        catch (ToolOperationException exception)
        {
            return output.Failure(1, exception.Code, exception.Message);
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
