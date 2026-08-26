using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

public sealed partial class ExtractionOrchestrator
{
    private static void CreateOwnedExecutionDirectories(OwnedAttemptPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.WorkingRoot);
            Directory.CreateDirectory(paths.OutputRoot);
            Directory.CreateDirectory(paths.StagingLogsRoot);
            if (!PathSafety.IsNormalDirectory(paths.StagingRoot) ||
                !PathSafety.IsNormalDirectory(paths.WorkingRoot) ||
                !PathSafety.IsNormalDirectory(paths.OutputRoot) ||
                !PathSafety.IsNormalDirectory(paths.StagingLogsRoot))
            {
                throw new InvalidOperationException(
                    "An extraction staging directory is unsafe.");
            }
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.ProcessStart,
                ExtractionFailureCode.ProcessStartFailed,
                "Atlas-owned Cpp2IL staging directories could not be prepared.",
                paths.AttemptId,
                exception);
        }
    }

    private static void ApplyProcessResult(
        ref ExtractionAttempt attempt,
        ExtractionProcessResult result)
    {
        attempt = attempt with
        {
            ProcessId = result.ProcessId ?? attempt.ProcessId,
            ProcessExitCode = result.ExitCode,
            StartedAtUtc = result.StartedAtUtc ?? attempt.StartedAtUtc,
            StandardOutputTruncated = result.StandardOutput.Truncated,
            StandardErrorTruncated = result.StandardError.Truncated,
            StandardOutputDiscardedBytes = result.StandardOutput.DiscardedBytes,
            StandardErrorDiscardedBytes = result.StandardError.DiscardedBytes
        };
    }

    private static void ApplyCanceledProcessResult(
        ref ExtractionAttempt attempt,
        Cpp2IlProcessCanceledException exception)
    {
        attempt = attempt with
        {
            ProcessId = exception.ProcessId ?? attempt.ProcessId,
            ProcessExitCode = null,
            StartedAtUtc = exception.StartedAtUtc ?? attempt.StartedAtUtc,
            StandardOutputTruncated = exception.StandardOutput.Truncated,
            StandardErrorTruncated = exception.StandardError.Truncated,
            StandardOutputDiscardedBytes = exception.StandardOutput.DiscardedBytes,
            StandardErrorDiscardedBytes = exception.StandardError.DiscardedBytes
        };
    }

    private void MoveProcessLogs(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        EnsureSafeStaging(paths);
        Directory.CreateDirectory(paths.FinalLogsRoot);
        if (!PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The final extraction log directory is unsafe.");
        }

        MoveExactLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stdout.log"),
            attempt.StandardOutputPath);
        MoveExactLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stderr.log"),
            attempt.StandardErrorPath);
        DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
    }

    private void PromoteCandidateOutput(OwnedAttemptPaths paths)
    {
        EnsureSafeStaging(paths);
        var output = InspectOwnedOutput(paths);
        _ = output;
        if (OwnedAttemptPaths.EntryExists(paths.CandidateOutputRoot))
        {
            throw new IOException("Candidate output already exists.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            paths.CandidateOutputRoot);
        _moveDirectory(paths.OutputRoot, paths.CandidateOutputRoot);
    }
}
