using S1Atlas.Application.Authority;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Docs.Generation;

public sealed record PortalSiteModel(
    string ResolvedBuildId,
    IReadOnlyList<PortalIndexModel> Indexes,
    PortalBuildHistoryModel BuildHistory,
    PortalEnvironmentModel? CurrentEnvironment,
    IReadOnlyList<PortalDiffModel> Diffs,
    IReadOnlyList<PortalStatus> Statuses);

public sealed record PortalIndexModel(
    IndexRunRecord Run,
    CodebaseKind Codebase,
    CodeChannel Channel,
    string IndexId,
    string SourceIdentity,
    string? BuildId,
    string? ExtractionId,
    bool IsVerifiedAuthority,
    IReadOnlyList<PortalNamespaceModel> Namespaces,
    int SymbolTotal);

public sealed record PortalNamespaceModel(
    string Name,
    IReadOnlyList<PortalSymbolModel> Symbols,
    int TotalCount);

public sealed record PortalSymbolModel(
    string IndexId,
    CodebaseKind Codebase,
    CodeChannel Channel,
    string SymbolId,
    string CanonicalKey,
    SymbolKind Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort,
    BodyRecoveryStatus? BodyRecoveryStatus,
    string PagePath,
    string Anchor,
    PortalSymbolEvidenceModel? Evidence = null);

public sealed record PortalSymbolEvidenceModel(
    PortalRelationshipEvidenceModel Relationships,
    PortalSourceResult Source,
    DerivedContext Context);

public sealed record PortalRelationshipEvidenceModel(
    IReadOnlyList<RelationshipQueryResult> References,
    int ReferenceTotal,
    IReadOnlyList<RelationshipQueryResult> Callers,
    int CallerTotal,
    IReadOnlyList<RelationshipQueryResult> Callees,
    int CalleeTotal,
    string CallerCompletenessNotice,
    string CalleeCompletenessNotice);

public sealed record PortalBuildHistoryModel(
    IReadOnlyList<PortalBuildEntry> Entries,
    IReadOnlyList<PortalDiffModel> AdjacentDiffs);

public sealed record PortalBuildEntry(
    GameBuild Build,
    InstalledBuildHistoryStatus Status,
    bool IsNavigable,
    string? CodePath);

public sealed record PortalEnvironmentModel(EnvironmentSnapshot Snapshot, string PagePath);

public sealed record PortalDiffModel(
    string BeforeBuildId,
    string AfterBuildId,
    BuildDiffResult Result,
    string PagePath);

public sealed record PortalStatus(string Code, string Label, bool IsError, string? Detail);

public enum PortalSourceState
{
    Available,
    NoIndexedLocation,
    IntegrityFailure,
    Unavailable
}

public sealed record PortalSourceResult(
    PortalSourceState State,
    SourceSnippetQueryResult? Snippet,
    string Label);

public sealed record DerivedStatement(string Text, string EvidenceHref);

public sealed record DerivedContext(
    IReadOnlyList<DerivedStatement> Overview,
    IReadOnlyList<DerivedStatement> ModderRelevance,
    IReadOnlyList<DerivedStatement> Learning);
