using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Inputs;

namespace S1Atlas.Extraction.Attempts;

internal sealed class ExtractionRecoveryService
{
    internal const string InterruptedMessage =
        "The extraction attempt was interrupted because its recorded processes are no longer running.";

    private readonly string _dataRoot;
    private readonly IExtractionRepository _repository;
    private readonly AttemptDocumentStore _documentStore;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly TimeProvider _timeProvider;

    public ExtractionRecoveryService(
        string dataRoot,
        IExtractionRepository repository,
        AttemptDocumentStore documentStore,
        Func<int, bool> isProcessAlive,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _repository = repository;
        _documentStore = documentStore;
        _isProcessAlive = isProcessAlive;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PathSafety.IsNormalDirectory(_dataRoot))
        {
            throw new InvalidOperationException("The Atlas data root is unsafe.");
        }

        var lockManager = new ExtractionLock(
            _dataRoot, ownerProcessId: 1, _isProcessAlive, _timeProvider);
        var lockState = await lockManager.ReadAsync(cancellationToken);
        if (lockState is not null)
        {
            if (IsAlive(lockState.OwnerProcessId) ||
                lockState.ChildProcessId is int lockChild && IsAlive(lockChild))
            {
                throw new ExtractionAlreadyActiveException(lockState.AttemptId);
            }
        }

        var repositoryAttempts = await _repository.ListNonTerminalAttemptsAsync(
            cancellationToken);
        var candidates = new List<RecoveryCandidate>();
        foreach (var attempt in repositoryAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRecoverable(attempt.Status))
            {
                continue;
            }

            var paths = OwnedAttemptPaths.Create(
                _dataRoot, attempt.BuildId, attempt.AttemptId);
            var hasDocument = OwnedAttemptPaths.EntryExists(paths.AttemptDocumentPath);
            var snapshot = await _documentStore.TryReadAsync(
                paths, attempt, cancellationToken);
            if (hasDocument && snapshot is null)
            {
                throw new InvalidDataException(
                    $"Attempt '{attempt.AttemptId}' has ambiguous document evidence.");
            }

            var facts = snapshot?.ExecutionFacts;
            if (facts?.OwnerProcessId > 0 && IsAlive(facts.OwnerProcessId))
            {
                throw new ExtractionAlreadyActiveException(attempt.AttemptId);
            }

            if (attempt.ProcessId is int childProcessId && IsAlive(childProcessId))
            {
                throw new ExtractionAlreadyActiveException(attempt.AttemptId);
            }

            if (lockState?.AttemptId == attempt.AttemptId &&
                lockState.ChildProcessId is int recordedChild && IsAlive(recordedChild))
            {
                throw new ExtractionAlreadyActiveException(attempt.AttemptId);
            }

            var output = InspectOwnedOutput(paths);
            InspectOwnedStaging(paths);
            InspectLogFinalization(paths, attempt);
            candidates.Add(new RecoveryCandidate(attempt, paths, facts, output));
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var abandoned = candidate.Attempt with
            {
                Status = ExtractionAttemptStatus.Abandoned,
                CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                FailureStage = ExtractionFailureStage.Recovery,
                FailureCode = ExtractionFailureCode.InterruptedProcess,
                FailureMessage = InterruptedMessage,
                DiscardedFileCount = checked(
                    candidate.Attempt.DiscardedFileCount +
                    (candidate.Attempt.KeepFailedArtifacts ? 0 : candidate.Output.FileCount)),
                DiscardedByteCount = checked(
                    candidate.Attempt.DiscardedByteCount +
                    (candidate.Attempt.KeepFailedArtifacts ? 0 : candidate.Output.ByteCount))
            };
            await _repository.TransitionAttemptAsync(
                abandoned, candidate.Attempt.Status, cancellationToken);
            FinalizeOwnedStaging(candidate.Paths, abandoned.KeepFailedArtifacts);
            await _documentStore.WriteAsync(
                candidate.Paths, abandoned, candidate.ExecutionFacts, cancellationToken);
        }

        if (lockState is not null)
        {
            await lockManager.DeleteStaleAsync(lockState, cancellationToken);
        }
    }

    private OwnedOutputFacts InspectOwnedOutput(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.OutputRoot))
        {
            return new OwnedOutputFacts(0, 0);
        }

        if (!PathSafety.IsNormalDirectory(paths.OutputRoot) ||
            !OwnedAttemptPaths.IsSameOrDescendant(paths.StagingRoot, paths.OutputRoot))
        {
            throw new InvalidDataException(
                $"Attempt '{paths.AttemptId}' has ambiguous staging output.");
        }

        if (Directory.Exists(paths.RetainedOutputRoot) || File.Exists(paths.RetainedOutputRoot))
        {
            throw new InvalidDataException(
                $"Attempt '{paths.AttemptId}' already has retained output evidence.");
        }

        var fileCount = 0;
        long byteCount = 0;
        var pending = new Stack<string>();
        pending.Push(paths.OutputRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Attempt '{paths.AttemptId}' output contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                fileCount = checked(fileCount + 1);
                byteCount = checked(byteCount + new FileInfo(entry).Length);
            }
        }

        return new OwnedOutputFacts(fileCount, byteCount);
    }

    private static void InspectOwnedStaging(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingRoot))
        {
            return;
        }

        if (!PathSafety.IsNormalDirectory(paths.StagingRoot) ||
            !OwnedAttemptPaths.PathsEqualParent(paths.StagingRoot, paths.AttemptId))
        {
            throw new InvalidDataException(
                $"Attempt '{paths.AttemptId}' has ambiguous staging evidence.");
        }

        var pending = new Stack<string>();
        pending.Push(paths.StagingRoot);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Attempt '{paths.AttemptId}' staging contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void FinalizeOwnedStaging(
        OwnedAttemptPaths paths,
        bool keepFailedArtifacts)
    {
        if (!Directory.Exists(paths.StagingRoot))
        {
            return;
        }

        Directory.CreateDirectory(paths.AttemptRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(paths.DataRoot, paths.AttemptRoot);
        MoveLogs(paths);

        if (Directory.Exists(paths.OutputRoot))
        {
            if (keepFailedArtifacts)
            {
                Directory.Move(paths.OutputRoot, paths.RetainedOutputRoot);
            }
            else
            {
                Directory.Delete(paths.OutputRoot, recursive: true);
            }
        }

        if (Directory.Exists(paths.StagingRoot))
        {
            Directory.Delete(paths.StagingRoot, recursive: true);
        }
    }

    private static void InspectLogFinalization(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        if (!Directory.Exists(paths.StagingLogsRoot))
        {
            return;
        }

        if (File.Exists(paths.FinalLogsRoot) ||
            Directory.Exists(paths.FinalLogsRoot) && !PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw new InvalidDataException("The final attempt log path is unsafe.");
        }

        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(attempt.StandardOutputPath),
            Path.GetFileName(attempt.StandardErrorPath)
        };
        foreach (var source in Directory.EnumerateFileSystemEntries(paths.StagingLogsRoot))
        {
            var attributes = File.GetAttributes(source);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !expectedNames.Contains(Path.GetFileName(source)))
            {
                throw new InvalidDataException(
                    "The staging log directory contains ambiguous evidence.");
            }

            if (File.Exists(Path.Combine(paths.FinalLogsRoot, Path.GetFileName(source))))
            {
                throw new InvalidDataException(
                    "Staging and final attempt logs contain conflicting evidence.");
            }
        }
    }

    private static void MoveLogs(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingLogsRoot))
        {
            return;
        }

        if (!Directory.Exists(paths.FinalLogsRoot))
        {
            Directory.Move(paths.StagingLogsRoot, paths.FinalLogsRoot);
            return;
        }

        if (!PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw new InvalidDataException("The final attempt log directory is unsafe.");
        }

        foreach (var source in Directory.EnumerateFiles(paths.StagingLogsRoot))
        {
            var destination = Path.Combine(paths.FinalLogsRoot, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                throw new InvalidDataException(
                    "Staging and final attempt logs contain conflicting evidence.");
            }

            File.Move(source, destination);
        }

        Directory.Delete(paths.StagingLogsRoot);
    }

    private bool IsAlive(int processId)
    {
        try
        {
            return _isProcessAlive(processId);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Process liveness for PID {processId} could not be proven.", exception);
        }
    }

    private static bool IsRecoverable(ExtractionAttemptStatus status) => status is
        ExtractionAttemptStatus.Created or
        ExtractionAttemptStatus.Preparing or
        ExtractionAttemptStatus.Running or
        ExtractionAttemptStatus.Validating;

    private sealed record RecoveryCandidate(
        ExtractionAttempt Attempt,
        OwnedAttemptPaths Paths,
        AttemptExecutionFacts? ExecutionFacts,
        OwnedOutputFacts Output);

    private readonly record struct OwnedOutputFacts(int FileCount, long ByteCount);
}
