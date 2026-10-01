using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Api;

public sealed record ServeIndexStatus(
    CodebaseKind Codebase,
    CodeChannel Channel,
    string IndexId,
    int SymbolCount);

public sealed record ServeStatusResult(
    string? ResolvedBuildId,
    InstalledBuildAuthorityStatus AuthorityStatus,
    string? Message,
    IReadOnlyList<ServeIndexStatus> Indexes);

public sealed record ServeBuildListItem(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid,
    bool IsCurrent,
    bool HasPreferredVerifiedExtraction,
    bool HasCompletedIndex);

public sealed record ServeBuildListResult(
    IReadOnlyList<ServeBuildListItem> Builds);

public sealed record ServeAdjacentDiff(
    string FromBuildId,
    string ToBuildId);

public sealed record ServeBuildResult(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid,
    bool IsCurrent,
    string Status,
    bool IsNavigable,
    string? Message,
    string? ExtractionId,
    string? IndexId,
    IReadOnlyList<ServeIndexStatus> Surfaces,
    IReadOnlyList<ServeAdjacentDiff> AdjacentDiffs,
    bool EnvironmentAvailable);
