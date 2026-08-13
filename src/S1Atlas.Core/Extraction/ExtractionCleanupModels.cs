namespace S1Atlas.Core.Extraction;

/// <summary>
/// The category of an Atlas-owned tree that conservative cleanup may consider.
/// </summary>
public enum ExtractionCleanupItemKind
{
    TerminalAttempt,
    ExtractionStaging,
    InputStaging,
    ToolStaging,
    ToolQuarantine
}

/// <summary>
/// A cleanup-eligible tree. Filesystem ownership and fingerprint details remain
/// internal to <c>S1Atlas.Extraction</c>; only these stable projections cross the
/// Core boundary.
/// </summary>
public sealed record ExtractionCleanupItem(
    ExtractionCleanupItemKind Kind,
    string Id,
    string? BuildId,
    string? AttemptId,
    string DisplayPath,
    DateTimeOffset ControllingTimestampUtc,
    int FileCount,
    long ByteCount);

/// <summary>
/// A tree that was recognized but withheld from deletion because its database
/// facts and filesystem ownership did not both agree it was safe to remove.
/// </summary>
public sealed record ExtractionCleanupBlockedItem(
    ExtractionCleanupItemKind Kind,
    string Id,
    string DisplayPath,
    string Code,
    string Message);

/// <summary>
/// The read-only result of cleanup planning. Aggregate counts reflect only the
/// eligible items so preview and apply report the same estimate.
/// </summary>
public sealed record ExtractionCleanupPlan(
    TimeSpan OlderThan,
    DateTimeOffset CutoffUtc,
    IReadOnlyList<ExtractionCleanupItem> EligibleItems,
    IReadOnlyList<ExtractionCleanupBlockedItem> BlockedItems)
{
    public int EligibleFileCount =>
        EligibleItems.Sum(item => item.FileCount);

    public long EligibleByteCount =>
        EligibleItems.Sum(item => item.ByteCount);
}

/// <summary>
/// A cleanup deletion that was attempted during apply but did not complete.
/// </summary>
public sealed record ExtractionCleanupFailure(
    ExtractionCleanupItemKind Kind,
    string Id,
    string Code,
    string Message);

/// <summary>
/// The outcome of a cleanup apply run. <see cref="HasOperationalProblems"/> is
/// false only when nothing was blocked during planning and no deletion failed.
/// </summary>
public sealed record ExtractionCleanupResult(
    ExtractionCleanupPlan Plan,
    bool Applied,
    IReadOnlyList<ExtractionCleanupItem> DeletedItems,
    IReadOnlyList<ExtractionCleanupFailure> Failures)
{
    public bool HasOperationalProblems =>
        Plan.BlockedItems.Count > 0 || Failures.Count > 0;
}
