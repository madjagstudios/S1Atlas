using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class IndexQueryService
{
    private readonly IIndexRepository _repository;
    private readonly string? _dataRoot;
    private readonly SymbolResolver _symbolResolver;
    private readonly SourceSnippetReader _sourceSnippetReader = new();

    public IndexQueryService(IIndexRepository repository)
        : this(repository, null)
    {
    }

    public IndexQueryService(IIndexRepository repository, string? dataRoot)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dataRoot = dataRoot is null ? null : Path.GetFullPath(dataRoot);
        _symbolResolver = new SymbolResolver(_repository);
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

    public async Task<SourceSnippetResolutionResult> SourceAsync(
        string selector,
        IndexQueryOptions options,
        int context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
        if (_dataRoot is null)
            throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");

        var resolved = new List<(CodeChannel Channel, IndexRunRecord Run, SymbolQueryResult Symbol)>();
        var ambiguous = new List<SymbolQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;

            var resolution = await _symbolResolver.ResolveAsync(
                run.IndexId,
                selector,
                options.Codebase,
                channel,
                cancellationToken);
            if (resolution.Status == SymbolResolutionStatus.Ambiguous)
            {
                ambiguous.AddRange(resolution.Candidates);
                continue;
            }
            if (resolution.Status == SymbolResolutionStatus.Resolved && resolution.Symbol is not null)
                resolved.Add((channel, run, resolution.Symbol));
        }

        if (ambiguous.Count > 0 || resolved.Count > 1)
        {
            var candidates = ambiguous
                .Concat(resolved.Select(item => item.Symbol))
                .GroupBy(candidate => candidate.SymbolId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.QualifiedName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Channel, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SymbolId, StringComparer.Ordinal)
                .ToArray();
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.Ambiguous, null, candidates),
                null);
        }

        if (resolved.Count == 0)
        {
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []),
                null);
        }

        var selected = resolved[0];
        var symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved symbol disappeared from the completed index.");
        var locations = await _repository.GetCompletedSourceLocationsAsync(selected.Run.IndexId, cancellationToken);
        var matchingLocations = locations
            .Where(location => string.Equals(location.SymbolId, selected.Symbol.SymbolId, StringComparison.Ordinal))
            .OrderBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ToArray();
        if (matchingLocations.Length == 0)
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
                null);
        if (matchingLocations.Length > 1)
            throw new InvalidDataException("The completed index contains multiple source locations for the selected symbol.");

        var locationRecord = matchingLocations[0];
        var files = await _repository.GetCompletedSourceFilesAsync(selected.Run.IndexId, cancellationToken);
        var sourceFile = files.SingleOrDefault(file =>
            string.Equals(file.SourceFileId, locationRecord.SourceFileId, StringComparison.Ordinal));
        if (sourceFile is null)
            throw new InvalidDataException("The selected symbol source location references a missing source file record.");

        var indexRoot = ResolveIndexRoot(_dataRoot, options.Codebase, selected.Channel, selected.Run.IndexId);
        var sourcePath = ResolveContainedSourcePath(indexRoot, sourceFile.RelativePath);
        var read = await _sourceSnippetReader.ReadAsync(
            sourcePath,
            sourceFile.Sha256,
            locationRecord,
            context,
            cancellationToken);
        var location = new SourceLocationQueryResult(
            locationRecord.SymbolId,
            locationRecord.StartLine,
            locationRecord.StartColumn,
            locationRecord.EndLine,
            locationRecord.EndColumn);
        var bodyRecoveryStatus = IsCallable(symbolRecord.Kind)
            ? symbolRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        var snippet = new SourceSnippetQueryResult(
            selected.Symbol,
            selected.Run.IndexId,
            sourceFile.RelativePath,
            sourceFile.Sha256,
            sourceFile.ByteCount,
            location,
            read.ContextBefore,
            read.ContextAfter,
            read.Text,
            bodyRecoveryStatus,
            options.Codebase + ":" + selected.Channel + ":generated");
        return new SourceSnippetResolutionResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            snippet);
    }

    private static string ResolveIndexRoot(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        if (codebase != CodebaseKind.ScheduleI || channel != CodeChannel.Installed)
            throw new NotSupportedException("Integrity-checked source path resolution is not yet available for this codebase/channel.");

        var buildsRoot = Path.Combine(dataRoot, "builds");
        if (!Directory.Exists(buildsRoot))
            throw new FileNotFoundException("The Atlas build index root was not found.", buildsRoot);

        var candidates = new List<string>();
        foreach (var buildRoot in Directory.EnumerateDirectories(buildsRoot))
        {
            if ((File.GetAttributes(buildRoot) & FileAttributes.ReparsePoint) != 0)
                continue;
            var candidate = Path.Combine(buildRoot, "indexes", indexId);
            if (Directory.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
                candidates.Add(Path.GetFullPath(candidate));
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("The completed Atlas index source root was not found."),
            _ => throw new InvalidDataException("Multiple Atlas-owned source roots matched the completed index identity.")
        };
    }

    private static string ResolveContainedSourcePath(string indexRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The indexed source path is not a safe relative path.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("The indexed source path escaped its Atlas-owned index root.");
        return fullPath;
    }

    private static bool IsCallable(string kind) =>
        string.Equals(kind, SymbolKind.Method.ToString(), StringComparison.Ordinal) ||
        string.Equals(kind, SymbolKind.Constructor.ToString(), StringComparison.Ordinal);

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
