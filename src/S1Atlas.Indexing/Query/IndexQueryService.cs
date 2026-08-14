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

    public async Task<IReadOnlyList<SymbolQueryResult>> SearchAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var results = new List<SymbolQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            var symbols = await _repository.GetCompletedSymbolsAsync(run.IndexId, cancellationToken);
            results.AddRange(symbols.Select(symbol => new SymbolQueryResult(run.IndexId, options.Codebase.ToString(), channel.ToString(), symbol.SymbolId, symbol.Kind, symbol.QualifiedName, symbol.Signature, symbol.IsBestEffort)));
        }

        return results
            .Where(result => result.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase) || result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(result => Rank(result, query))
            .ThenBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ThenBy(result => result.Signature, StringComparer.Ordinal)
            .ThenBy(result => result.Channel, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> FindAsync(
        string query,
        SymbolKind kind,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        return (await SearchAsync(query, options, cancellationToken))
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
        var name = ExtractName(result.QualifiedName);
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (result.QualifiedName.Split('.').Any(segment => string.Equals(segment, query, StringComparison.OrdinalIgnoreCase))) return 1;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static string ExtractName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        if (separator < 0) return qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..];
        var member = qualifiedName[(separator + 2)..];
        var end = member.IndexOfAny(['(', ':']);
        if (end >= 0) member = member[..end];
        var space = member.LastIndexOf(' ');
        return space >= 0 ? member[(space + 1)..] : member;
    }
}
