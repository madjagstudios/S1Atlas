using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class IndexQueryService
{
    private readonly IIndexRepository _repository;

    public IndexQueryService(IIndexRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<SymbolSearchResult> SearchAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The query result limit must be positive.");

        var totalCount = 0;
        var candidates = new List<SymbolQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;

            var count = await _repository.CountCompletedSymbolMatchesAsync(run.IndexId, query, cancellationToken);
            totalCount += count;
            if (count == 0) continue;

            var symbols = await _repository.SearchCompletedSymbolsAsync(
                run.IndexId,
                query,
                options.Limit,
                cancellationToken);
            candidates.AddRange(symbols.Select(symbol =>
                SymbolResolver.ToQueryResult(run.IndexId, options.Codebase, channel, symbol)));
        }

        var results = candidates
            .OrderBy(result => Rank(result, query))
            .ThenBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ThenBy(result => result.Signature, StringComparer.Ordinal)
            .ThenBy(result => result.Channel, StringComparer.Ordinal)
            .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
            .Take(options.Limit)
            .ToArray();
        return new SymbolSearchResult(totalCount, results.Length, results);
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> FindAsync(
        string query,
        SymbolKind kind,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        return (await SearchAsync(query, options, cancellationToken)).Results
            .Where(result => string.Equals(result.Kind, kind.ToString(), StringComparison.Ordinal))
            .ToArray();
    }

    public async Task<IReadOnlyList<RelationshipQueryResult>> RelationshipsAsync(
        string query,
        bool callers,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<RelationshipQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            var symbols = await _repository.GetCompletedSymbolsAsync(run.IndexId, cancellationToken);
            var matching = symbols.Where(symbol => symbol.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase) || symbol.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(symbol => symbol.SymbolId).ToHashSet(StringComparer.Ordinal);
            var edges = await _repository.GetCompletedRelationshipsAsync(run.IndexId, cancellationToken);
            results.AddRange(edges.Where(edge => callers
                ? matching.Contains(edge.TargetSymbolId ?? string.Empty) || (edge.TargetText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                : matching.Contains(edge.SourceSymbolId)).Select(edge => new RelationshipQueryResult(edge.RelationshipId, edge.Kind, edge.Evidence, edge.SourceSymbolId, edge.TargetSymbolId, edge.TargetText)));
        }
        return results.OrderBy(result => result.RelationshipId, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<SourceQueryResult>> SourceAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<SourceQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            var symbols = await _repository.GetCompletedSymbolsAsync(run.IndexId, cancellationToken);
            if (!symbols.Any(symbol => symbol.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase) || symbol.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            var files = await _repository.GetCompletedSourceFilesAsync(run.IndexId, cancellationToken);
            var locations = await _repository.GetCompletedSourceLocationsAsync(run.IndexId, cancellationToken);
            results.AddRange(files.Select(file => new SourceQueryResult(
                run.IndexId,
                file.RelativePath,
                file.Sha256,
                file.ByteCount,
                options.Codebase + ":" + channel + ":generated",
                locations
                    .Where(location => location.SourceFileId == file.SourceFileId)
                    .Select(location => new SourceLocationQueryResult(location.SymbolId, location.StartLine, location.StartColumn, location.EndLine, location.EndColumn))
                    .ToArray())));
        }
        return results.OrderBy(result => result.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<CodeChannel> Channels(IndexQueryOptions options)
    {
        if (!options.AllChannels) return [options.Channel ?? CodeChannel.Installed];
        if (options.Codebase == CodebaseKind.ScheduleI) return [CodeChannel.Installed];
        return [CodeChannel.Installed, CodeChannel.Release, CodeChannel.Preview];
    }

    private static int Rank(SymbolQueryResult result, string query)
    {
        if (string.Equals(result.QualifiedName, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Signature, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (result.QualifiedName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (result.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (result.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        return 5;
    }
}
