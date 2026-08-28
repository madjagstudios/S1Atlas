using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Workflow;

public sealed record PersistedGameSymbols(
    string IndexId,
    string SnapshotId,
    string BuildId,
    string VerifiedExtractionIdentity,
    IReadOnlyList<IndexSymbolRecord> Symbols);

public sealed class ReferenceGameSymbolLoader
{
    private readonly IIndexRepository _repository;

    public ReferenceGameSymbolLoader(IIndexRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<PersistedGameSymbols> LoadAsync(string gameIndexId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameIndexId);
        var index = await _repository.GetCompletedIndexAsync(gameIndexId, cancellationToken)
            ?? throw new InvalidOperationException("The requested base game index is not completed.");
        var snapshot = await _repository.GetCodeSnapshotAsync(index.SnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("The requested base game index has no code snapshot.");
        if (snapshot.Codebase != CodebaseKind.ScheduleI || snapshot.Channel != CodeChannel.Installed)
            throw new InvalidOperationException("Reference indexing requires a completed installed Schedule I index.");
        var buildId = await _repository.GetCompletedIndexBuildIdAsync(gameIndexId, cancellationToken)
            ?? throw new InvalidOperationException("The requested base game index has no verified build identity.");
        return new PersistedGameSymbols(
            gameIndexId,
            index.SnapshotId,
            buildId,
            snapshot.SourceIdentity,
            await _repository.GetCompletedSymbolsAsync(gameIndexId, cancellationToken));
    }
}
