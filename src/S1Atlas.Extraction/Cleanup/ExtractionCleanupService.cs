using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// Thrown before any mutation when a live extraction currently holds the Atlas lock, so
/// cleanup never races an in-flight extraction.
/// </summary>
internal sealed class ExtractionCleanupActiveException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Runs recovery, then a fresh conservative plan, and (for apply) deletes only proven
/// unchanged candidates. Every candidate is re-observed immediately before deletion and
/// its aggregate observation digest compared; a changed candidate is preserved as a
/// failure. Filesystem deletion always precedes the matching terminal-attempt database
/// deletion, so a database failure leaves a truthful, idempotently retryable state.
/// </summary>
internal sealed class ExtractionCleanupService
{
    private readonly Func<CancellationToken, Task> _initializeAsync;
    private readonly Func<CancellationToken, Task> _recoverAsync;
    private readonly Func<CancellationToken, Task<bool>> _isExtractionActiveAsync;
    private readonly ExtractionCleanupPlanner _planner;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly CleanupTreeInspector _treeInspector;
    private readonly Func<string, CancellationToken, Task> _deleteOwnedTreeAsync;

    public ExtractionCleanupService(
        Func<CancellationToken, Task> initializeAsync,
        Func<CancellationToken, Task> recoverAsync,
        Func<CancellationToken, Task<bool>> isExtractionActiveAsync,
        ExtractionCleanupPlanner planner,
        IValidatedExtractionRepository validatedRepository,
        CleanupTreeInspector treeInspector,
        Func<string, CancellationToken, Task> deleteOwnedTreeAsync)
    {
        _initializeAsync = initializeAsync
            ?? throw new ArgumentNullException(nameof(initializeAsync));
        _recoverAsync = recoverAsync ?? throw new ArgumentNullException(nameof(recoverAsync));
        _isExtractionActiveAsync = isExtractionActiveAsync
            ?? throw new ArgumentNullException(nameof(isExtractionActiveAsync));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _validatedRepository = validatedRepository
            ?? throw new ArgumentNullException(nameof(validatedRepository));
        _treeInspector = treeInspector ?? throw new ArgumentNullException(nameof(treeInspector));
        _deleteOwnedTreeAsync = deleteOwnedTreeAsync
            ?? throw new ArgumentNullException(nameof(deleteOwnedTreeAsync));
    }

    public async Task<ExtractionCleanupPlan> PreviewAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken)
    {
        await PrepareAsync(cancellationToken);
        var planning = await _planner.PlanAsync(olderThan, cancellationToken);
        return planning.PublicPlan;
    }

    public async Task<ExtractionCleanupResult> ApplyAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken)
    {
        await PrepareAsync(cancellationToken);
        // A fresh plan is always created after repository initialization and recovery.
        var planning = await _planner.PlanAsync(olderThan, cancellationToken);

        var deleted = new List<ExtractionCleanupItem>();
        var failures = new List<ExtractionCleanupFailure>();
        foreach (var candidate in planning.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyCandidateAsync(candidate, deleted, failures, cancellationToken);
        }

        return new ExtractionCleanupResult(
            planning.PublicPlan,
            Applied: true,
            deleted,
            failures);
    }

    private async Task ApplyCandidateAsync(
        CleanupCandidate candidate,
        List<ExtractionCleanupItem> deleted,
        List<ExtractionCleanupFailure> failures,
        CancellationToken cancellationToken)
    {
        var item = candidate.PublicItem;
        if (!ReObservationMatches(candidate))
        {
            failures.Add(new ExtractionCleanupFailure(
                item.Kind,
                item.Id,
                "CleanupEvidenceChanged",
                "The cleanup candidate changed since planning and was preserved."));
            return;
        }

        try
        {
            // Filesystem deletion precedes the database deletion so an interrupted apply
            // leaves a retryable row rather than an orphaned tree.
            foreach (var ownedPath in candidate.OwnedPaths)
            {
                await _deleteOwnedTreeAsync(ownedPath, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(new ExtractionCleanupFailure(
                item.Kind,
                item.Id,
                "CleanupFilesystemDeleteFailed",
                $"The cleanup filesystem deletion failed: {exception.Message}"));
            return;
        }

        if (candidate.ExpectedAttemptStatus is { } status &&
            candidate.ExpectedCompletedAtUtc is { } completedAtUtc)
        {
            try
            {
                await _validatedRepository.DeleteCleanupEligibleAttemptAsync(
                    item.Id,
                    status,
                    completedAtUtc,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new ExtractionCleanupFailure(
                    item.Kind,
                    item.Id,
                    "CleanupDatabaseDeleteFailed",
                    $"The terminal attempt row deletion failed after its files were " +
                    $"removed and remains retryable: {exception.Message}"));
                return;
            }
        }

        deleted.Add(item);
    }

    private bool ReObservationMatches(CleanupCandidate candidate)
    {
        var perPathDigests = new List<string>(candidate.OwnedPaths.Count);
        foreach (var ownedPath in candidate.OwnedPaths)
        {
            var inspection = _treeInspector.Inspect(ownedPath, allowMissing: true);
            if (inspection.Outcome != CleanupObservationOutcome.Observed)
            {
                return false;
            }

            perPathDigests.Add(inspection.Observation!.ObservationDigest);
        }

        return string.Equals(
            CleanupObservationAggregate.Digest(perPathDigests),
            candidate.ObservationDigest,
            StringComparison.Ordinal);
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _initializeAsync(cancellationToken);
        await _recoverAsync(cancellationToken);
        if (await _isExtractionActiveAsync(cancellationToken))
        {
            throw new ExtractionCleanupActiveException(
                "An extraction is currently active; cleanup will not run while the " +
                "Atlas extraction lock is held.");
        }
    }
}
