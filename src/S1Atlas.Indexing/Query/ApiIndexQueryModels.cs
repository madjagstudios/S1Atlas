using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

public enum ApiIndexAvailability
{
    Current,
    Stale,
    Unavailable,
    Ambiguous
}

public sealed record ApiIndexSelection(
    CodebaseKind Codebase,
    CodeChannel Channel,
    ApiIndexAvailability Availability,
    string? IndexId,
    string? SnapshotId,
    string? SourceIdentity,
    string? EnvironmentSnapshotId,
    string? Message);

public sealed record ApiIndexCatalogResult(
    IReadOnlyList<ApiIndexSelection> Selections,
    string? RequestedBuildId,
    string? ResolvedBuildId);
