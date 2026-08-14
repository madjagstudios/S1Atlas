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

    public Task<RelationshipQuerySetResult> RefsAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Refs, cancellationToken);

    public Task<RelationshipQuerySetResult> CallersAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callees, cancellationToken);

    private async Task<RelationshipQuerySetResult> RelationshipSetAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipQueryMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
        {
            return new RelationshipQuerySetResult(
                selection.Resolution,
                [],
                null,
                mode == RelationshipQueryMode.Callers,
                string.Empty);
        }

        var selected = selection.Selected.Value;
        var symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved symbol disappeared from the completed index.");

        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges;
        if (mode == RelationshipQueryMode.Refs)
        {
            var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken);
            var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken);
            selectedEdges = outgoing
                .Select(edge => (edge, "Outgoing"))
                .Concat(incoming.Select(edge => (edge, "Incoming")))
                .GroupBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }
        else if (mode == RelationshipQueryMode.Callers)
        {
            var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken);
            selectedEdges = incoming
                .Where(edge => IsCallLike(edge.Kind))
                .Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken);
            selectedEdges = outgoing
                .Where(edge => IsCallLike(edge.Kind))
                .Select(edge => (edge, "Outgoing"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }

        var endpointIds = selectedEdges
            .SelectMany(item => item.Edge.TargetSymbolId is null
                ? new[] { item.Edge.SourceSymbolId }
                : new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var endpointSymbols = await _repository.GetCompletedSymbolsByIdsAsync(
            selected.Run.IndexId,
            endpointIds,
            cancellationToken);
        var byId = endpointSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);

        var relationships = selectedEdges
            .Select(item => new RelationshipQueryResult(
                item.Edge.RelationshipId,
                item.Edge.Kind,
                item.Edge.Evidence,
                item.Direction,
                Endpoint(item.Edge.SourceSymbolId, null, byId),
                Endpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, byId)))
            .ToArray();

        BodyRecoveryStatus? bodyStatus = IsCallable(symbolRecord.Kind)
            ? symbolRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        var notice = mode == RelationshipQueryMode.Refs
            ? string.Empty
            : CompletenessNotice(bodyStatus, mode == RelationshipQueryMode.Callers);
        return new RelationshipQuerySetResult(
            selection.Resolution,
            relationships,
            bodyStatus,
            mode == RelationshipQueryMode.Callers,
            notice);
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

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new SourceSnippetResolutionResult(selection.Resolution, null);

        var selected = selection.Selected.Value;
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
            return new SourceSnippetResolutionResult(selection.Resolution, null);
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
        BodyRecoveryStatus? bodyRecoveryStatus = IsCallable(symbolRecord.Kind)
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
        return new SourceSnippetResolutionResult(selection.Resolution, snippet);
    }

    private async Task<ChannelSelection> ResolveAcrossChannelsAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var resolved = new List<SelectedSymbol>();
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
                resolved.Add(new SelectedSymbol(channel, run, resolution.Symbol));
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
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.Ambiguous, null, candidates),
                null);
        }

        if (resolved.Count == 0)
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []),
                null);

        return new ChannelSelection(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, resolved[0].Symbol, []),
            resolved[0]);
    }

    private static RelationshipEndpointQueryResult Endpoint(
        string? symbolId,
        string? rawText,
        IReadOnlyDictionary<string, IndexSymbolRecord> byId)
    {
        if (symbolId is not null && byId.TryGetValue(symbolId, out var symbol))
            return new RelationshipEndpointQueryResult(
                symbol.SymbolId,
                symbol.QualifiedName,
                symbol.Signature,
                null,
                true);

        return new RelationshipEndpointQueryResult(
            symbolId,
            null,
            null,
            rawText,
            false);
    }

    private static bool IsCallLike(string kind) =>
        string.Equals(kind, "Calls", StringComparison.Ordinal) ||
        string.Equals(kind, "Constructs", StringComparison.Ordinal);

    private static string CompletenessNotice(BodyRecoveryStatus? status, bool callers)
    {
        var bodyNotice = status switch
        {
            BodyRecoveryStatus.Recovered => "Atlas has affirmative recovered-body evidence.",
            BodyRecoveryStatus.NoBodyByDesign => "No implementation body is expected for this declaration.",
            BodyRecoveryStatus.StubOrUnavailable => "The body is stubbed or unavailable; zero call results are not definitive.",
            BodyRecoveryStatus.Unknown => "Body recovery is unknown; zero call results are not definitive.",
            null => "Call completeness is not applicable to a non-callable symbol.",
            _ => "Body recovery status is unrecognized; zero call results are not definitive."
        };
        return callers
            ? bodyNotice + " Incoming callers are limited to call sites whose target resolved to the selected symbol."
            : bodyNotice;
    }

    private static string ResolveIndexRoot(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        var candidates = new List<string>();
        foreach (var candidate in EnumerateIndexRoots(dataRoot, codebase, channel, indexId))
        {
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

    private static IEnumerable<string> EnumerateIndexRoots(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        if (codebase == CodebaseKind.ScheduleI && channel == CodeChannel.Installed)
        {
            var buildsRoot = Path.Combine(dataRoot, "builds");
            if (!Directory.Exists(buildsRoot)) yield break;
            foreach (var buildRoot in Directory.EnumerateDirectories(buildsRoot))
            {
                if ((File.GetAttributes(buildRoot) & FileAttributes.ReparsePoint) != 0) continue;
                yield return Path.Combine(buildRoot, "indexes", indexId);
            }
            yield break;
        }

        if (codebase is not (CodebaseKind.S1Api or CodebaseKind.S1MApi))
            throw new NotSupportedException("Integrity-checked source path resolution is not available for this codebase/channel.");
        var segment = codebase == CodebaseKind.S1Api ? "s1api" : "s1mapi";
        var root = channel == CodeChannel.Installed
            ? Path.Combine(dataRoot, "installed", segment)
            : channel is CodeChannel.Release or CodeChannel.Preview
                ? Path.Combine(dataRoot, "upstream", segment, "commits")
                : throw new NotSupportedException("Integrity-checked source path resolution is not available for this codebase/channel.");
        if (!Directory.Exists(root)) yield break;
        foreach (var child in Directory.EnumerateDirectories(root))
            yield return Path.Combine(child, "indexes", indexId);
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

    private enum RelationshipQueryMode
    {
        Refs,
        Callers,
        Callees
    }

    private readonly record struct SelectedSymbol(
        CodeChannel Channel,
        IndexRunRecord Run,
        SymbolQueryResult Symbol);

    private readonly record struct ChannelSelection(
        SymbolResolutionResult Resolution,
        SelectedSymbol? Selected);
}
