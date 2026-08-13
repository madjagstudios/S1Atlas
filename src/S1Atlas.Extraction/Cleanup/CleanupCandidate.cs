using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// An eligible cleanup tree paired with the private facts apply needs but Core does
/// not expose: the exact Atlas-owned paths, the canonical aggregate observation digest
/// that apply must reproduce before deleting, and (for terminal attempts only) the
/// expected database status and completion timestamp for the matching row deletion.
/// </summary>
internal sealed record CleanupCandidate(
    ExtractionCleanupItem PublicItem,
    IReadOnlyList<string> OwnedPaths,
    string ObservationDigest,
    ExtractionAttemptStatus? ExpectedAttemptStatus,
    DateTimeOffset? ExpectedCompletedAtUtc);

/// <summary>
/// The result of read-only cleanup planning: the public plan shown in preview/apply
/// output and the internal candidates apply consumes in deterministic order.
/// </summary>
internal sealed record CleanupPlanningResult(
    ExtractionCleanupPlan PublicPlan,
    IReadOnlyList<CleanupCandidate> Candidates);
