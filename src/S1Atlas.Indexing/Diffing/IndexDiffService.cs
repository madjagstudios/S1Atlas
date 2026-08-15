using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Diffing;

public sealed class IndexDiffException : Exception
{
    public IndexDiffException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class IndexDiffService
{
    private readonly IIndexRepository _repository;
    private readonly SymbolDiffEngine _engine;

    public IndexDiffService(IIndexRepository repository, SymbolDiffEngine? engine = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _engine = engine ?? new SymbolDiffEngine();
    }

    public async Task<IndexDiffResult> CompareAsync(
        string fromIndexId,
        string toIndexId,
        CancellationToken cancellationToken)
    {
        var from = await LoadAsync(fromIndexId, cancellationToken);
        var to = await LoadAsync(toIndexId, cancellationToken);
        if (from.Snapshot.Codebase != to.Snapshot.Codebase)
            throw new IndexDiffException("NotComparable", "The selected snapshots belong to different codebases.");

        try
        {
            return _engine.Compare(from, to);
        }
        catch (ArgumentException exception)
        {
            throw new IndexDiffException("NotComparable", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            throw new IndexDiffException("InvalidIndexData", exception.Message);
        }
    }

    public async Task<IndexRunRecord> ResolveSelectorAsync(
        CodebaseKind codebase,
        string selector,
        CodeChannel defaultChannel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var channel = ParseChannel(selector) ?? defaultChannel;
        if (ParseChannel(selector) is not null)
        {
            return await _repository.GetLatestCompletedIndexAsync(codebase, channel, null, cancellationToken)
                ?? throw new IndexDiffException("NoCompletedIndex", $"No completed index exists for {codebase} {channel}.");
        }

        var directIndex = await _repository.GetCompletedIndexAsync(selector, cancellationToken);
        if (directIndex is not null)
        {
            var directSnapshot = await _repository.GetCodeSnapshotAsync(directIndex.SnapshotId, cancellationToken)
                ?? throw new IndexDiffException("SnapshotNotFound", $"The completed index '{selector}' references a missing snapshot.");
            EnsureCodebase(codebase, directSnapshot);
            return directIndex;
        }

        var snapshot = await _repository.GetCodeSnapshotAsync(selector, cancellationToken);
        if (snapshot is not null)
        {
            EnsureCodebase(codebase, snapshot);
            return await _repository.GetLatestCompletedIndexForSnapshotAsync(snapshot.SnapshotId, cancellationToken)
                ?? throw MissingSelector(selector);
        }

        var matchingRuns = new List<IndexRunRecord>();
        foreach (var candidateChannel in Enum.GetValues<CodeChannel>().OrderBy(candidate => candidate == channel ? 0 : 1))
        {
            var candidate = await _repository.GetLatestCompletedIndexBySourceIdentityAsync(codebase, candidateChannel, selector, cancellationToken);
            if (candidate is not null)
                matchingRuns.Add(candidate);
        }

        return matchingRuns.Count switch
        {
            1 => matchingRuns[0],
            > 1 => throw new IndexDiffException("AmbiguousSnapshot", $"The source selector '{selector}' matches multiple channels."),
            _ => throw MissingSelector(selector)
        };
    }

    private async Task<IndexSnapshotFacts> LoadAsync(string indexId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        var run = await _repository.GetCompletedIndexAsync(indexId, cancellationToken)
            ?? throw new IndexDiffException("SnapshotNotFound", $"No completed index was found for '{indexId}'.");
        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken)
            ?? throw new IndexDiffException("SnapshotNotFound", $"The completed index '{indexId}' references a missing snapshot.");
        return new IndexSnapshotFacts(
            snapshot,
            run,
            await _repository.GetCompletedSymbolsAsync(indexId, cancellationToken),
            await _repository.GetCompletedFingerprintsAsync(indexId, cancellationToken),
            await _repository.GetCompletedRelationshipsAsync(indexId, cancellationToken));
    }

    private static IndexDiffException MissingSelector(string selector) =>
        new("SnapshotNotFound", $"No completed index matches snapshot selector '{selector}'.");

    private static void EnsureCodebase(CodebaseKind requested, CodeSnapshotRecord snapshot)
    {
        if (snapshot.Codebase != requested)
            throw new IndexDiffException("NotComparable", $"The selector resolves to {snapshot.Codebase}, not {requested}.");
    }

    private static CodeChannel? ParseChannel(string selector) => selector.ToLowerInvariant() switch
    {
        "installed" => CodeChannel.Installed,
        "release" => CodeChannel.Release,
        "preview" => CodeChannel.Preview,
        _ => null
    };
}
