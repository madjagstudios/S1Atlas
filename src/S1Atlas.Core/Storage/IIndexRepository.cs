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
    BodyRecoveryStatus? BodyRecoveryStatus = null,
    bool IsPublic = false);

public enum CallableSurfaceKind
{
    DirectGameMember,
    PublicMethodWrapper,
    PublicFieldAccessor,
    PublicPropertyAccessor,
    NonPublicWrapper
}

public enum CallableSurfaceStatus
{
    Resolved,
    Ambiguous,
    Unavailable
}

public enum InteropInputTrust
{
    LocalOnly
}

public sealed record IndexCallableSurfaceRecord(
    string CallableSurfaceId,
    string IndexId,
    string SnapshotId,
    string GameSymbolId,
    string GameCanonicalKey,
    string InteropAssemblyName,
    string? InteropInputSha256,
    string? InteropSignature,
    CallableSurfaceKind Kind,
    bool RequiresReflection,
    CallableSurfaceStatus Status,
    InteropInputTrust InteropInputTrust,
    string Evidence);

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
    IReadOnlyList<IndexRelationshipRecord> Relationships,
    IReadOnlyList<IndexCallableSurfaceRecord>? CallableSurface = null,
    ReferenceIndexContextRecord? ReferenceIndexContext = null,
    IReadOnlyList<IndexReferenceModRecord>? ReferenceMods = null,
    IReadOnlyList<IndexReferenceDocumentRecord>? ReferenceDocuments = null);

public interface INativeRecoveryRepository<TRecord, TRequest>
    where TRecord : class
    where TRequest : class
{
    Task SaveNativeRecoveryAsync(TRecord record, CancellationToken cancellationToken);
    Task<TRecord?> GetNativeRecoveryAsync(string recoveryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TRecord>> GetNativeRecoveriesAsync(
        TRequest request,
        CancellationToken cancellationToken);
}

public interface IIndexRepository
{
    ISceneRepository RequireSceneRepository() =>
        this as ISceneRepository ?? throw new InvalidOperationException(
            "Scene indexing requires an index repository that also owns scene persistence.");

    INativeRecoveryRepository<TRecord, TRequest> RequireNativeRecoveryRepository<TRecord, TRequest>()
        where TRecord : class
        where TRequest : class =>
        this as INativeRecoveryRepository<TRecord, TRequest> ?? throw new InvalidOperationException(
            "Native recovery requires an index repository that also owns native evidence persistence.");

    Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken);
    Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken);
    Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken);
    Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken);
    Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken);
    Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken);
    Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolPageAsync(string indexId, int offset, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Bounded symbol paging is not supported by this index repository.");
    Task<int> CountCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Completed symbol counting is not supported by this index repository.");
    Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(
        string indexId,
        string canonicalKey,
        CancellationToken cancellationToken);
    Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken);
    Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null);
    Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken);
    Task<int> CountCompletedRelationshipsByTargetTextAsync(
        string indexId,
        string targetText,
        RelationshipTargetTextMatchMode matchMode,
        string relationshipKind,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(
        string indexId,
        string targetText,
        RelationshipTargetTextMatchMode matchMode,
        string relationshipKind,
        int limit,
        CancellationToken cancellationToken);
    Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        string relationshipKind,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        string relationshipKind,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceAsync(string indexId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexCallableSurfaceRecord>>([]);
    Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceByGameSymbolIdAsync(
        string indexId,
        string gameSymbolId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexCallableSurfaceRecord>>([]);
    Task<ReferenceIndexContextRecord?> GetReferenceIndexContextAsync(string indexId, CancellationToken cancellationToken) =>
        Task.FromResult<ReferenceIndexContextRecord?>(null);
    Task<IndexRunRecord?> GetLatestCompletedReferenceIndexAsync(string collection, CancellationToken cancellationToken) =>
        GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind.ReferenceMod, CodeChannel.Installed, collection, cancellationToken);
    Task<IReadOnlyList<IndexRunRecord>> GetCompletedReferenceIndexesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexRunRecord>>([]);
    Task<IReadOnlyList<IndexReferenceModRecord>> GetCompletedReferenceModsAsync(string indexId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexReferenceModRecord>>([]);
    Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(string indexId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexReferenceDocumentRecord>>([]);
    async Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(
        string indexId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        return (await GetCompletedReferenceDocumentsAsync(indexId, cancellationToken)).Take(limit).ToArray();
    }
    Task<IReadOnlyList<IndexReferenceDocumentRecord>> SearchCompletedReferenceDocumentsAsync(
        string indexId,
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexReferenceDocumentRecord>>([]);
    Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken);
    Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken);
    Task<string?> GetCompletedIndexBuildIdAsync(string indexId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
