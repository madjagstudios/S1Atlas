using S1Atlas.Core.Storage;

namespace S1Atlas.Application.Authority;

public enum InstalledBuildAuthorityStatus
{
    Resolved,
    NoCurrentBuild,
    BuildNotFound,
    NoPreferredVerifiedExtraction,
    ExtractionIntegrityFailure,
    NoCompletedIndex,
    IndexBuildMismatch
}

public sealed record InstalledBuildAuthority(
    InstalledBuildAuthorityStatus Status,
    string? RequestedBuildId,
    string? ResolvedBuildId,
    string? ExtractionId,
    string? IndexId,
    IndexRunRecord? IndexRun,
    string? Message);
