using S1Atlas.Core.Indexing;

namespace S1Atlas.Core.Storage;

public enum IndexRunStatus
{
    Running,
    Completed,
    Failed
}

public sealed record CodeSnapshotRecord(
    string SnapshotId,
    CodebaseKind Codebase,
    CodeChannel Channel,
    string SourceIdentity,
    string CreatedAtUtc,
    string? EnvironmentSnapshotId = null);

public sealed record IndexRunRecord(
    string IndexId,
    string SnapshotId,
    IndexRunStatus Status,
    string StartedAtUtc,
    string? CompletedAtUtc = null,
    string? FailureMessage = null);

public sealed record IndexSymbolRecord(
    string SymbolId,
    string SnapshotId,
    string CanonicalKey,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort,
    BodyRecoveryStatus? BodyRecoveryStatus = null);

public sealed record IndexSourceFileRecord(
    string SourceFileId,
    string SnapshotId,
    string RelativePath,
    string Sha256,
    long ByteCount);

public sealed record IndexSourceLocationRecord(
    string SymbolId,
    string SourceFileId,
    int StartLine,
    int StartColumn,
    int? EndLine = null,
    int? EndColumn = null);

public sealed record IndexFingerprintRecord(
    string SymbolId,
    string Kind,
    string Fingerprint);

public sealed record IndexRelationshipRecord(
    string RelationshipId,
    string SnapshotId,
    string SourceSymbolId,
    string? TargetSymbolId,
    string? TargetText,
    string Kind,
    string Evidence);

public sealed record IndexWriteSet(
    IReadOnlyList<IndexSymbolRecord> Symbols,
    IReadOnlyList<IndexSourceFileRecord> SourceFiles,
    IReadOnlyList<IndexSourceLocationRecord> SourceLocations,
    IReadOnlyList<IndexFingerprintRecord> Fingerprints,
    IReadOnlyList<IndexRelationshipRecord> Relationships);

public interface IIndexRepository
{
    Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken);
    Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken);
    Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken);
    Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken);
    Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken);
    Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken);
    Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken);
}
