namespace S1Atlas.Core.Indexing;

public sealed record IndexQueryOptions(
    CodebaseKind Codebase,
    CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false,
    int Limit = 50);

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
    Ambiguous
}

public sealed record SymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates);

public sealed record SymbolSearchResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SymbolQueryResult> Results);

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
