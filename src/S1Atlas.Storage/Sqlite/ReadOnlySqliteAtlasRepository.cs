using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;

namespace S1Atlas.Storage.Sqlite;

public sealed class ReadOnlySqliteAtlasRepository :
    IAtlasRepository,
    IIndexRepository,
    ISceneRepository,
    IValidatedExtractionRepository
{
    private const string ReadOnlyMessage = "S1Atlas MCP is read-only.";
    private readonly SqliteAtlasRepository _repository;

    public ReadOnlySqliteAtlasRepository(ReadOnlySqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var databaseDirectory =
            Path.GetDirectoryName(Path.GetFullPath(factory.DatabasePath)) ?? ".";
        var backupDirectory = Path.Combine(databaseDirectory, "backups");
        _repository = new SqliteAtlasRepository(factory.DatabasePath, backupDirectory);
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task SaveSnapshotAsync(
        EnvironmentSnapshot snapshot,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken) =>
        _repository.GetCurrentSnapshotAsync(cancellationToken);

    public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(
        CancellationToken cancellationToken) =>
        _repository.ListBuildsAsync(cancellationToken);

    public Task CreateCodeSnapshotAsync(
        CodeSnapshotRecord snapshot,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken) =>
        _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken);

    public Task StartIndexRunAsync(
        IndexRunRecord run,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task CompleteIndexRunAsync(
        string indexId,
        IndexWriteSet writeSet,
        string completedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task FailIndexRunAsync(
        string indexId,
        string failureMessage,
        string completedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<IndexRunRecord?> GetCompletedIndexAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedIndexAsync(indexId, cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string? environmentSnapshotId,
        CancellationToken cancellationToken) =>
        _repository.GetLatestCompletedIndexAsync(
            codebase,
            channel,
            environmentSnapshotId,
            cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSymbolsAsync(indexId, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(
        string indexId,
        string canonicalKey,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSymbolByCanonicalKeyAsync(
            indexId,
            canonicalKey,
            cancellationToken);

    public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken);

    public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(
        string indexId,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSymbolsByIdsAsync(indexId, symbolIds, cancellationToken);

    public Task<int> CountCompletedSymbolMatchesAsync(
        string indexId,
        string query,
        CancellationToken cancellationToken,
        string? kind = null) =>
        _repository.CountCompletedSymbolMatchesAsync(indexId, query, cancellationToken, kind);

    public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(
        string indexId,
        string query,
        int limit,
        CancellationToken cancellationToken,
        string? kind = null) =>
        _repository.SearchCompletedSymbolsAsync(indexId, query, limit, cancellationToken, kind);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedRelationshipsAsync(indexId, cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
            indexId,
            symbolId,
            cancellationToken);

    public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(
        string indexId,
        string symbolId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
            indexId,
            symbolId,
            cancellationToken);

    public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSourceFilesAsync(indexId, cancellationToken);

    public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSourceLocationsAsync(indexId, cancellationToken);

    public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(
        string indexId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedFingerprintsAsync(indexId, cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string sourceIdentity,
        CancellationToken cancellationToken) =>
        _repository.GetLatestCompletedIndexBySourceIdentityAsync(
            codebase,
            channel,
            sourceIdentity,
            cancellationToken);

    public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string buildId,
        CancellationToken cancellationToken) =>
        _repository.GetLatestCompletedIndexForBuildAsync(
            codebase,
            channel,
            buildId,
            cancellationToken);

    public Task CreateSceneSnapshotAsync(
        SceneSnapshotRecord snapshot,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task StartSceneSnapshotAsync(
        string sceneSnapshotId,
        string startedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task CompleteSceneSnapshotAsync(
        string sceneSnapshotId,
        SceneWriteSet writeSet,
        string completedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task FailSceneSnapshotAsync(
        string sceneSnapshotId,
        string failureCode,
        string failureMessage,
        string completedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task PublishSceneSnapshotAsync(
        string sceneSnapshotId,
        string publishedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(
        string sceneSnapshotId,
        CancellationToken cancellationToken) =>
        _repository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, cancellationToken);

    public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(
        string buildId,
        CancellationToken cancellationToken) =>
        _repository.GetLatestCompletedSceneSnapshotAsync(buildId, cancellationToken);

    public Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(
        string sceneSnapshotId,
        CancellationToken cancellationToken) =>
        _repository.GetSceneIndexStatisticsAsync(sceneSnapshotId, cancellationToken);

    public Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(
        string sceneSnapshotId,
        IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken) =>
        _repository.GetSceneContainersAsync(sceneSnapshotId, containerIds, cancellationToken);

    public Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(
        SceneListQueryOptions options,
        CancellationToken cancellationToken) =>
        _repository.ListScenesAsync(options, cancellationToken);

    public Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(
        string sceneSnapshotId,
        string name,
        SceneDocumentKind? kind,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.FindScenesByExactNameAsync(sceneSnapshotId, name, kind, limit, cancellationToken);

    public Task<SceneDocumentRecord?> GetSceneAsync(
        string sceneSnapshotId,
        string sceneId,
        CancellationToken cancellationToken) =>
        _repository.GetSceneAsync(sceneSnapshotId, sceneId, cancellationToken);

    public Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(
        GameObjectListQueryOptions options,
        CancellationToken cancellationToken) =>
        _repository.ListGameObjectsAsync(options, cancellationToken);

    public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(
        string sceneSnapshotId,
        string sceneId,
        string name,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.FindGameObjectsByExactNameAsync(
            sceneSnapshotId,
            sceneId,
            name,
            limit,
            cancellationToken);

    public Task<SceneGameObjectRecord?> GetGameObjectAsync(
        string sceneSnapshotId,
        string gameObjectId,
        CancellationToken cancellationToken) =>
        _repository.GetGameObjectAsync(sceneSnapshotId, gameObjectId, cancellationToken);

    public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(
        ComponentListQueryOptions options,
        CancellationToken cancellationToken) =>
        _repository.ListComponentsAsync(options, cancellationToken);

    public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(
        string sceneSnapshotId,
        string selector,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.FindComponentsByExactTypeAsync(sceneSnapshotId, selector, limit, cancellationToken);

    public Task<SceneComponentRecord?> GetComponentAsync(
        string sceneSnapshotId,
        string componentId,
        CancellationToken cancellationToken) =>
        _repository.GetComponentAsync(sceneSnapshotId, componentId, cancellationToken);

    public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(
        ReferenceListQueryOptions options,
        CancellationToken cancellationToken) =>
        _repository.ListReferencesAsync(options, cancellationToken);

    public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
        string recipeId,
        CancellationToken cancellationToken) =>
        _repository.ListProcessCompletedAttemptsAsync(recipeId, cancellationToken);

    public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
        string extractionId,
        CancellationToken cancellationToken) =>
        _repository.GetValidatedExtractionAsync(extractionId, cancellationToken);

    public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
        string extractionId,
        CancellationToken cancellationToken) =>
        _repository.GetExtractionArtifactsAsync(extractionId, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
        string? buildId,
        CancellationToken cancellationToken) =>
        _repository.ListValidatedExtractionsAsync(buildId, cancellationToken);

    public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken) =>
        _repository.ListValidatedExtractionsByRecipeAsync(recipeId, cancellationToken);

    public Task<StoredValidationResult?> GetLatestValidationResultAsync(
        string extractionId,
        string policyDigest,
        CancellationToken cancellationToken) =>
        _repository.GetLatestValidationResultAsync(extractionId, policyDigest, cancellationToken);

    public Task<PreferredExtraction?> GetPreferredExtractionAsync(
        string buildId,
        CancellationToken cancellationToken) =>
        _repository.GetPreferredExtractionAsync(buildId, cancellationToken);

    public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
        string? buildId,
        CancellationToken cancellationToken) =>
        _repository.ListAttemptsAsync(buildId, cancellationToken);

    public Task SaveValidationFailureAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task CommitValidatedExtractionAsync(
        ValidatedExtractionPromotion promotion,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task LinkAttemptToValidatedExtractionAsync(
        ValidationPersistence validation,
        ValidatedExtraction extraction,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task SaveRevalidationAsync(
        ValidationPersistence validation,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task SetPreferredExtractionAsync(
        PreferredExtraction preference,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task ClearPreferredExtractionAsync(
        string buildId,
        string expectedExtractionId,
        ExtractionPreferenceReason reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);

    public Task DeleteCleanupEligibleAttemptAsync(
        string attemptId,
        ExtractionAttemptStatus expectedStatus,
        DateTimeOffset expectedCompletedAtUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyMessage);
}
