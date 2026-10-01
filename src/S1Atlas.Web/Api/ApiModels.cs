using S1Atlas.Application.Authority;
using S1Atlas.Core.Environment;
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

public sealed record ServeEnvironmentDependency(
    DependencyKind Kind,
    string? Version,
    string? Path,
    bool IsInstalled,
    string? BinarySha256);

public sealed record ServeEnvironmentResult(
    string BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath,
    IReadOnlyList<ServeEnvironmentDependency> Dependencies);

public sealed record ServeDiffCounts(
    int Added,
    int Removed,
    int MethodBodyChanged,
    int RelationshipsChanged,
    int Unchanged);

// SymbolId is set only when the change links to a symbol page that
// resolves: the canonical key must exist in the to-index and in the
// current index, because symbol IDs are snapshot-scoped hashes.
public sealed record ServeDiffChange(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    DiffClassification Classification,
    string? SignatureBefore,
    string? SignatureAfter,
    string? SymbolId);

public sealed record ServeDiffResult(
    string FromBuildId,
    string ToBuildId,
    string FromIndexId,
    string ToIndexId,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    ServeDiffCounts Counts,
    int TotalChanged,
    int Page,
    int PageSize,
    IReadOnlyList<ServeDiffChange> Changes);

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
