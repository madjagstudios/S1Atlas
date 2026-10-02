using S1Atlas.Core.Storage;

namespace S1Atlas.Core.Indexing;

public sealed record IndexQueryOptions(
    CodebaseKind Codebase,
    CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false,
    int Limit = 50,
    IndexQueryScope Scope = IndexQueryScope.Game,
    string? ReferenceCollection = null);

public enum IndexQueryScope
{
    Game,
    Reference,
    All
}

public sealed record IndexPageRequest
{
    public IndexPageRequest(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        Offset = offset;
        Limit = limit;
    }

    public int Offset { get; }
    public int Limit { get; }
}

public sealed record IndexedSymbolPageResult(
    int TotalCount,
    IReadOnlyList<IndexedSymbolQueryResult> Results,
    bool HasMore);

public sealed record IndexedSymbolQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string SymbolId,
    string CanonicalKey,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort,
    BodyRecoveryStatus? BodyRecoveryStatus,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    string? RelativePath = null,
    string? Sha256 = null);

public sealed record NamespaceQueryResult(
    int TotalCount,
    IReadOnlyList<string> Namespaces);

public sealed record IndexSelectionQueryResult(
    IndexRunRecord Run,
    CodeSnapshotRecord Snapshot);

public sealed record RelationshipEvidenceQueryResult(
    IReadOnlyList<RelationshipQueryResult> References,
    int ReferenceTotal,
    IReadOnlyList<RelationshipQueryResult> Callers,
    int CallerTotal,
    IReadOnlyList<RelationshipQueryResult> Callees,
    int CalleeTotal,
    string CallerCompletenessNotice,
    string CalleeCompletenessNotice);

public enum RuntimeVerificationSignal
{
    Physics,
    NavMesh,
    TriggerState
}

public sealed record RuntimeVerificationHint(
    IReadOnlyList<RuntimeVerificationSignal> Signals,
    string Message);

public sealed record SymbolQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string SymbolId,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    string? RelativePath = null,
    string? Sha256 = null,
    string? ShortId = null);

public enum SymbolResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    NoCompletedIndex
}

public record SymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates,
    IReadOnlyList<SymbolQueryResult>? Suggestions = null,
    int? TotalCandidateCount = null)
{
    public IReadOnlyList<SymbolQueryResult> Suggestions { get; init; } = Suggestions ?? [];
}

public sealed record KindedSymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates,
    SymbolQueryResult KindMismatch,
    IReadOnlyList<SymbolQueryResult>? Suggestions = null,
    int? TotalCandidateCount = null)
    : SymbolResolutionResult(Status, Symbol, Candidates, Suggestions, TotalCandidateCount);

public sealed record SymbolSearchResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SymbolQueryResult> Results,
    SymbolResolutionStatus? ResolutionStatus = null,
    string? SearchNotice = null);

public sealed record RelationshipEndpointQueryResult(
    string? SymbolId,
    string? QualifiedName,
    string? Signature,
    string? RawText,
    bool Resolved,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    string? RelativePath = null,
    string? Sha256 = null);

public enum RelationshipTargetTextMatchMode
{
    Exact,
    Prefix
}

public sealed record RelationshipQueryResult(
    string RelationshipId,
    string Kind,
    string Evidence,
    string Direction,
    RelationshipEndpointQueryResult Source,
    RelationshipEndpointQueryResult Target,
    bool IsDerived = false,
    IReadOnlyList<string>? Routes = null,
    string? GeneratedDetail = null,
    string? Label = null)
{
    public RelationshipQueryResult(
        string relationshipId,
        string kind,
        string evidence,
        string sourceSymbolId,
        string? targetSymbolId,
        string? targetText)
        : this(
            relationshipId,
            kind,
            evidence,
            string.Empty,
            new RelationshipEndpointQueryResult(sourceSymbolId, null, null, null, true),
            targetSymbolId is null
                ? new RelationshipEndpointQueryResult(null, null, null, targetText, false)
                : new RelationshipEndpointQueryResult(targetSymbolId, null, null, targetText, true))
    {
    }

    public string SourceSymbolId => Source.SymbolId ?? string.Empty;
    public string? TargetSymbolId => Target.SymbolId;
    public string? TargetText => Target.RawText;
}

public enum RelationshipLabelContext
{
    Callers,
    Callees,
    Readers,
    Writers,
    All,
    Refs
}

public static class RelationshipLabels
{
    public static string? ForRelationship(string kind, string evidence, RelationshipLabelContext context)
    {
        if (string.Equals(kind, nameof(RelationshipKind.ReferencesMethod), StringComparison.Ordinal))
            return string.Equals(evidence, nameof(RelationshipEvidence.Metadata), StringComparison.Ordinal)
                ? "metadata reference (not called)"
                : "delegate created (not called)";
        if (string.Equals(kind, nameof(RelationshipKind.TakesFieldAddress), StringComparison.Ordinal))
        {
            if (string.Equals(evidence, nameof(RelationshipEvidence.Metadata), StringComparison.Ordinal))
                return "metadata reference (not read)";
            return context == RelationshipLabelContext.Readers
                ? "possible read (address taken)"
                : "possible write (address taken)";
        }

        return null;
    }
}

public sealed record RelationshipQueryPageResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<RelationshipQueryResult> Relationships)
{
    public int TotalCount { get; init; } = RequireNonnegative(TotalCount, nameof(TotalCount));
    public int ReturnedCount { get; init; } = RequireNonnegative(ReturnedCount, nameof(ReturnedCount));
    public IReadOnlyList<RelationshipQueryResult> Relationships { get; init; } = RequireRelationships(TotalCount, ReturnedCount, Relationships);

    private static int RequireNonnegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static IReadOnlyList<RelationshipQueryResult> RequireRelationships(
        int totalCount,
        int returnedCount,
        IReadOnlyList<RelationshipQueryResult> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        if (returnedCount != relationships.Count)
            throw new ArgumentException("ReturnedCount must equal Relationships.Count.", nameof(returnedCount));
        if (returnedCount > totalCount)
            throw new ArgumentException("ReturnedCount must not exceed TotalCount.", nameof(returnedCount));

        return relationships;
    }
}

public sealed record RelationshipQuerySetResult(
    SymbolResolutionResult Resolution,
    IReadOnlyList<RelationshipQueryResult> Relationships,
    BodyRecoveryStatus? BodyRecoveryStatus,
    bool CallerCompletenessBoundedByTargetResolution,
    string CompletenessNotice,
    int? TotalCount = null,
    int? ExactCount = null,
    int? DerivedCount = null);

public sealed record HierarchyNodeQueryResult(
    RelationshipQueryResult Edge,
    int Depth,
    bool IsDirect);

public sealed record HierarchyQueryResult(
    SymbolResolutionResult Resolution,
    IReadOnlyList<HierarchyNodeQueryResult> Nodes,
    int TotalCount,
    int ReturnedCount)
{
    public SymbolResolutionResult Resolution { get; init; } = Resolution ?? throw new ArgumentNullException(nameof(Resolution));
    public IReadOnlyList<HierarchyNodeQueryResult> Nodes { get; init; } = Nodes ?? throw new ArgumentNullException(nameof(Nodes));
    public int TotalCount { get; init; } = RequireNonnegative(TotalCount, nameof(TotalCount));
    public int ReturnedCount { get; init; } = RequireReturnedCount(TotalCount, ReturnedCount, Nodes);

    private static int RequireNonnegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static int RequireReturnedCount(int totalCount, int returnedCount, IReadOnlyList<HierarchyNodeQueryResult> nodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(returnedCount, nameof(returnedCount));
        if (returnedCount != nodes.Count)
            throw new ArgumentException("ReturnedCount must equal Nodes.Count.", nameof(returnedCount));
        if (returnedCount > totalCount)
            throw new ArgumentException("ReturnedCount must not exceed TotalCount.", nameof(returnedCount));

        return returnedCount;
    }
}

public sealed record SourceQueryResult(
    string IndexId,
    string RelativePath,
    string Sha256,
    long ByteCount,
    string Provenance,
    IReadOnlyList<SourceLocationQueryResult> Locations,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null);

public sealed record SourceLocationQueryResult(
    string SymbolId,
    int StartLine,
    int StartColumn,
    int? EndLine,
    int? EndColumn);

public sealed record SourceSnippetQueryResult(
    SymbolQueryResult Symbol,
    string IndexId,
    string RelativePath,
    string Sha256,
    long ByteCount,
    SourceLocationQueryResult Location,
    int ContextBefore,
    int ContextAfter,
    string Text,
    BodyRecoveryStatus? BodyRecoveryStatus,
    string Provenance,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    RuntimeVerificationHint? RuntimeVerification = null,
    RelationshipEvidenceQueryResult? Neighborhood = null,
    string? NeighborhoodNotice = null);

public sealed record SourceSnippetResolutionResult(
    SymbolResolutionResult Resolution,
    SourceSnippetQueryResult? Snippet);

public sealed record CallableSurfaceQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string GameSymbolId,
    string GameCanonicalKey,
    string Kind,
    string Status,
    bool RequiresReflection,
    string? InteropAssemblyName,
    string? InteropInputSha256,
    string? InteropSignature,
    string InteropInputTrust,
    string Evidence);

public sealed record CallableSurfaceResolutionResult(
    SymbolResolutionResult Resolution,
    CallableSurfaceQueryResult? CallableSurface);

public sealed record ReferenceModQueryResult(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string RootPath,
    string ContentSha256,
    string? Collection = null,
    string? Origin = "reference",
    string Provenance = "LocalOnly");

public sealed record ReferenceCollectionListResult(
    int TotalCount,
    IReadOnlyList<ReferenceCollectionQueryResult> Collections);

public sealed record ReferenceCollectionAuthorityQueryResult(
    string Collection,
    string ReferenceIndexId,
    string BuildId,
    string BaseIndexId);

public sealed record ReferenceCollectionQueryResult(
    string Collection,
    string IndexId,
    string SnapshotId,
    string BuildId,
    string BaseIndexId,
    int ModCount,
    IReadOnlyList<ReferenceCollectionModQueryResult> Mods);

public sealed record ReferenceCollectionModQueryResult(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string ContentSha256,
    string Provenance = "LocalOnly");

public sealed record ReferenceDocumentQueryResult(
    string ModId,
    string RelativePath,
    string Kind,
    string Sha256,
    long ByteCount,
    string Content,
    string? Collection = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    string? Origin = "reference",
    string Provenance = "LocalOnly")
{
    public string ReferenceModId => ModId;
}
