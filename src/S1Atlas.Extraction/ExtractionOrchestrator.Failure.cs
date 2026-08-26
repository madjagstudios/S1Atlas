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
    private async Task<TerminalFailureResult> FinalizeTerminalFailureAsync(
        ExtractionAttempt attempt,
        AttemptExecutionFacts? executionFacts,
        OwnedAttemptPaths paths,
        ExtractionAttemptStatus persistedStatus,
        ExtractionOperationException failure,
        bool keepFailedArtifacts,
        bool preserveStagingOutput)
    {
        FinalizeFailureLogs(paths, attempt);
        var discarded = new OutputFacts(0, 0);
        var effectiveFailure = failure;
        if (!preserveStagingOutput && Directory.Exists(paths.OutputRoot))
        {
            try
            {
                discarded = InspectOwnedOutput(paths);
                if (keepFailedArtifacts)
                {
                    if (OwnedAttemptPaths.EntryExists(paths.RetainedOutputRoot))
                    {
                        throw FilesystemFailure(
                            paths.AttemptId,
                            "Retained output already exists.");
                    }

                    OwnedAttemptPaths.EnsureSafeExistingPath(
                        paths.AttemptRoot,
                        paths.RetainedOutputRoot);
                    _moveDirectory(paths.OutputRoot, paths.RetainedOutputRoot);
                    discarded = new OutputFacts(0, 0);
                }
                else
                {
                    Directory.Delete(paths.OutputRoot, recursive: true);
                }
            }
            catch (Exception exception)
            {
                discarded = new OutputFacts(0, 0);
                effectiveFailure = exception as ExtractionOperationException ??
                    FilesystemFailure(
                        paths.AttemptId,
                        "Failed Cpp2IL output could not be safely finalized.",
                        exception);
            }
        }

        var canceled = effectiveFailure.Code == ExtractionFailureCode.OperationCanceled;
        var terminal = attempt with
        {
            Status = canceled
                ? ExtractionAttemptStatus.Canceled
                : ExtractionAttemptStatus.Failed,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            FailureStage = canceled ? null : effectiveFailure.Stage,
            FailureCode = canceled ? null : effectiveFailure.Code,
            FailureMessage = canceled ? null : effectiveFailure.Message,
            KeepFailedArtifacts = keepFailedArtifacts,
            DiscardedFileCount = checked(attempt.DiscardedFileCount + discarded.FileCount),
            DiscardedByteCount = checked(attempt.DiscardedByteCount + discarded.ByteCount),
            CandidateOutputPath = null,
            ResultExtractionId = null
        };
        await TransitionAsync(
            terminal,
            executionFacts,
            paths,
            persistedStatus,
            CancellationToken.None);
        return new TerminalFailureResult(terminal, effectiveFailure);
    }

    private void FinalizeFailureLogs(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        Directory.CreateDirectory(paths.FinalLogsRoot);
        if (!PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The final extraction log directory is unsafe.");
        }

        FinalizeOneFailureLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stdout.log"),
            attempt.StandardOutputPath);
        FinalizeOneFailureLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stderr.log"),
            attempt.StandardErrorPath);
        DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
    }

    private void FinalizeOneFailureLog(
        OwnedAttemptPaths paths,
        string stagingPath,
        string finalPath)
    {
        if (File.Exists(stagingPath))
        {
            MoveExactLog(paths, stagingPath, finalPath);
            return;
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            finalPath,
            allowFinalFile: true);
        if (!File.Exists(finalPath))
        {
            using var stream = new FileStream(
                finalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
    }

    private void MoveExactLog(
        OwnedAttemptPaths paths,
        string source,
        string destination)
    {
        if (!PathSafety.TryObserveRegularFile(paths.StagingLogsRoot, source, out _))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                $"Expected staging log '{Path.GetFileName(source)}' is missing or unsafe.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            destination,
            allowFinalFile: true);
        if (File.Exists(destination))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                $"Final log '{Path.GetFileName(destination)}' already exists.");
        }

        File.Move(source, destination);
    }
}
