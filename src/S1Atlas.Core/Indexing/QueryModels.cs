using S1Atlas.Core.Storage;

namespace S1Atlas.Core.Indexing;

public sealed record IndexQueryOptions(
    CodebaseKind Codebase,
    CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false,
    int Limit = 50);

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
    BodyRecoveryStatus? BodyRecoveryStatus);

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

public sealed record SymbolQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string SymbolId,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort);

public enum SymbolResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    NoCompletedIndex
}

public sealed record SymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates);

public sealed record SymbolSearchResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SymbolQueryResult> Results,
    SymbolResolutionStatus? ResolutionStatus = null);

public sealed record RelationshipEndpointQueryResult(
    string? SymbolId,
    string? QualifiedName,
    string? Signature,
    string? RawText,
    bool Resolved);

public sealed record RelationshipQueryResult(
    string RelationshipId,
    string Kind,
    string Evidence,
    string Direction,
    RelationshipEndpointQueryResult Source,
    RelationshipEndpointQueryResult Target)
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

public sealed record RelationshipQuerySetResult(
    SymbolResolutionResult Resolution,
    IReadOnlyList<RelationshipQueryResult> Relationships,
    BodyRecoveryStatus? BodyRecoveryStatus,
    bool CallerCompletenessBoundedByTargetResolution,
    string CompletenessNotice);

public sealed record SourceQueryResult(
    string IndexId,
    string RelativePath,
    string Sha256,
    long ByteCount,
    string Provenance,
    IReadOnlyList<SourceLocationQueryResult> Locations);

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
    string Provenance);

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
    string ContentSha256);

public sealed record ReferenceDocumentQueryResult(
    string ModId,
    string RelativePath,
    string Kind,
    string Sha256,
    long ByteCount,
    string Content);
