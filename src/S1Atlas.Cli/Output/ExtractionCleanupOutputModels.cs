namespace S1Atlas.Cli.Output;

internal sealed record ExtractionCleanupOutput(
    bool Applied,
    string OlderThan,
    string CutoffUtc,
    int EligibleFileCount,
    long EligibleByteCount,
    IReadOnlyList<ExtractionCleanupItemOutput> EligibleItems,
    IReadOnlyList<ExtractionCleanupBlockedOutput> BlockedItems,
    IReadOnlyList<ExtractionCleanupItemOutput> DeletedItems,
    IReadOnlyList<ExtractionCleanupFailureOutput> Failures);

internal sealed record ExtractionCleanupItemOutput(
    string Kind,
    string Id,
    string? BuildId,
    string? AttemptId,
    string DisplayPath,
    string ControllingTimestampUtc,
    int FileCount,
    long ByteCount);

internal sealed record ExtractionCleanupBlockedOutput(
    string Kind,
    string Id,
    string DisplayPath,
    string Code,
    string Message);

internal sealed record ExtractionCleanupFailureOutput(
    string Kind,
    string Id,
    string Code,
    string Message);
