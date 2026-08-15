using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Diffing;

public sealed record IndexSnapshotFacts(
    CodeSnapshotRecord Snapshot,
    IndexRunRecord Run,
    IReadOnlyList<IndexSymbolRecord> Symbols,
    IReadOnlyList<IndexFingerprintRecord> Fingerprints,
    IReadOnlyList<IndexRelationshipRecord> Relationships);

public enum SymbolChangeKind
{
    Added,
    Removed,
    SignatureChanged,
    StructuralChanged,
    BodyChanged,
    BodyUnavailable,
    RelationshipsChanged,
    SourceChanged,
    Unchanged
}

public enum RelationshipChangeKind
{
    Added,
    Removed
}

public sealed record SymbolDiffEvidence(
    string Layer,
    string? From,
    string? To);

public sealed record DiffEndpoint(
    string? SymbolId,
    string? ComparisonKey,
    string? QualifiedName,
    string? Signature,
    string? RawText,
    bool Resolved);

public sealed record RelationshipDiff(
    string Kind,
    RelationshipChangeKind Change,
    DiffEndpoint Source,
    DiffEndpoint Target,
    string Evidence);

public sealed record SymbolDiff(
    string ComparisonKey,
    string LineageKey,
    string? FromSymbolId,
    string? ToSymbolId,
    string? FromQualifiedName,
    string? ToQualifiedName,
    string? FromSignature,
    string? ToSignature,
    IReadOnlyList<SymbolChangeKind> Kinds,
    IReadOnlyList<SymbolDiffEvidence> Evidence,
    IReadOnlyList<RelationshipDiff> Relationships,
    BodyRecoveryStatus? FromBodyRecoveryStatus,
    BodyRecoveryStatus? ToBodyRecoveryStatus)
{
    public bool IsMeaningfulChange =>
        Kinds.Any(kind => kind is not SymbolChangeKind.Unchanged and not SymbolChangeKind.BodyUnavailable) ||
        (Kinds.Contains(SymbolChangeKind.BodyUnavailable) && FromBodyRecoveryStatus != ToBodyRecoveryStatus);
}

public sealed record IndexDiffResult(
    string FromIndexId,
    string ToIndexId,
    CodeSnapshotRecord From,
    CodeSnapshotRecord To,
    string FromFidelityBasis,
    string ToFidelityBasis,
    IReadOnlyList<SymbolDiff> Changes);
