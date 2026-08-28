using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class ReferenceModQueryService
{
    public const int MaxDocumentExcerptCharacters = 16 * 1024;
    private const int CandidateLimit = 50;
    private readonly IIndexRepository _repository;
    private readonly string? _dataRoot;
    private readonly SymbolResolver _symbolResolver;
    private readonly SourceSnippetReader _sourceSnippetReader = new();

    public ReferenceModQueryService(IIndexRepository repository, string? dataRoot = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dataRoot = dataRoot is null ? null : Path.GetFullPath(dataRoot);
        _symbolResolver = new SymbolResolver(_repository);
    }

    public async Task<SymbolResolutionResult> ResolveAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null)
            return NoCompletedIndex();

        var reference = await ResolveInIndexAsync(selection, selector, cancellationToken);
        if (reference.Status != SymbolResolutionStatus.NotFound)
            return reference;

        var gameRun = await _repository.GetCompletedIndexAsync(selection.Context.GameIndexId, cancellationToken);
        if (gameRun is null)
            return NoCompletedIndex();
        var game = await _symbolResolver.ResolveAsync(
            gameRun.IndexId,
            selector,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            cancellationToken);
        return DecorateGameResolution(game, selection.Collection);
    }

    public async Task<SymbolSearchResult> SearchAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        SymbolKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateLimit(options.Limit);
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null)
            return new SymbolSearchResult(0, 0, [], SymbolResolutionStatus.NoCompletedIndex);

        var kindName = kind?.ToString();
        var total = await _repository.CountCompletedSymbolMatchesAsync(selection.Run.IndexId, query, cancellationToken, kindName);
        IReadOnlyList<IndexSymbolRecord> symbols = total == 0
            ? []
            : await _repository.SearchCompletedSymbolsAsync(selection.Run.IndexId, query, options.Limit, cancellationToken, kindName);
        var results = symbols
            .Select(symbol => DecorateReferenceSymbol(selection, symbol))
            .OrderBy(result => Rank(result, query))
            .ThenBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ThenBy(result => result.Signature, StringComparer.Ordinal)
            .ThenBy(result => result.ReferenceModId, StringComparer.Ordinal)
            .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
            .Take(options.Limit)
            .ToArray();
        return new SymbolSearchResult(
            total,
            results.Length,
            results,
            total == 0 ? SymbolResolutionStatus.NotFound : SymbolResolutionStatus.Resolved);
    }

    public async Task<SourceSnippetResolutionResult> SourceAsync(
        string selector,
        IndexQueryOptions options,
        int context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0) throw new ArgumentOutOfRangeException(nameof(context));
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null) return new SourceSnippetResolutionResult(NoCompletedIndex(), null);

        var resolution = await ResolveInIndexAsync(selection, selector, cancellationToken);
        if (resolution.Status == SymbolResolutionStatus.NotFound)
        {
            var game = await ResolveAsync(selector, options, cancellationToken);
            if (game.Status != SymbolResolutionStatus.Resolved || game.Symbol is null)
                return new SourceSnippetResolutionResult(game, null);
            var gameResult = await new IndexQueryService(_repository, _dataRoot).SourceAsync(
                selector,
                new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
                context,
                cancellationToken);
            return gameResult with
            {
                Resolution = game,
                Snippet = gameResult.Snippet is null
                    ? null
                    : gameResult.Snippet with { Symbol = game.Symbol }
            };
        }
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new SourceSnippetResolutionResult(resolution, null);

        var symbol = await _repository.GetCompletedSymbolByIdAsync(selection.Run.IndexId, resolution.Symbol.SymbolId, cancellationToken)
            ?? throw new InvalidDataException("The resolved reference symbol disappeared from the completed index.");
        var locations = (await _repository.GetCompletedSourceLocationsAsync(selection.Run.IndexId, cancellationToken))
            .Where(location => string.Equals(location.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
            .OrderBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ToArray();
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(selection.Run.IndexId, cancellationToken);
        if (locations.Length == 0)
        {
            var fallbackFile = sourceFiles
                .Where(file => file.RelativePath.StartsWith((resolution.Symbol.ReferenceModId ?? string.Empty) + "/", StringComparison.Ordinal))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fallbackFile is null)
                return new SourceSnippetResolutionResult(resolution, null);
            if (_dataRoot is null)
                throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");
            var fallbackRoot = ResolveReferenceIndexRoot(_dataRoot, selection.Run.IndexId);
            var fallbackPath = ResolveContainedPath(fallbackRoot, fallbackFile.RelativePath);
            var bytes = await _sourceSnippetReader.ReadVerifiedBytesAsync(fallbackPath, fallbackFile.Sha256, cancellationToken);
            var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var fallbackLines = text.Split('\n');
            var fallbackLocation = new SourceLocationQueryResult(
                resolution.Symbol.SymbolId,
                1,
                1,
                Math.Max(1, fallbackLines.Length),
                Math.Max(1, fallbackLines[^1].Length + 1));
            var fallbackSnippet = new SourceSnippetQueryResult(
                resolution.Symbol,
                selection.Run.IndexId,
                fallbackFile.RelativePath,
                fallbackFile.Sha256,
                fallbackFile.ByteCount,
                fallbackLocation,
                0,
                0,
                text,
                IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : null,
                "ReferenceMod:Installed:generated",
                "reference",
                selection.Collection,
                resolution.Symbol.ReferenceModId,
                resolution.Symbol.DisplayName,
                resolution.Symbol.Version,
                resolution.Symbol.License);
            return new SourceSnippetResolutionResult(resolution, fallbackSnippet);
        }
        if (locations.Length > 1)
            throw new InvalidDataException("The completed reference index contains multiple source locations for the selected symbol.");

        var location = locations[0];
        var sourceFile = sourceFiles
            .SingleOrDefault(file => string.Equals(file.SourceFileId, location.SourceFileId, StringComparison.Ordinal));
        if (sourceFile is null)
            throw new InvalidDataException("The reference source location references a missing source file record.");
        if (_dataRoot is null)
            throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");

        var indexRoot = ResolveReferenceIndexRoot(_dataRoot, selection.Run.IndexId);
        var sourcePath = ResolveContainedPath(indexRoot, sourceFile.RelativePath);
        var read = await _sourceSnippetReader.ReadAsync(sourcePath, sourceFile.Sha256, location, context, cancellationToken);
        BodyRecoveryStatus? bodyStatus = IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : null;
        var queryLocation = new SourceLocationQueryResult(location.SymbolId, location.StartLine, location.StartColumn, location.EndLine, location.EndColumn);
        var snippet = new SourceSnippetQueryResult(
            resolution.Symbol,
            selection.Run.IndexId,
            sourceFile.RelativePath,
            sourceFile.Sha256,
            sourceFile.ByteCount,
            queryLocation,
            read.ContextBefore,
            read.ContextAfter,
            read.Text,
            bodyStatus,
            "ReferenceMod:Installed:generated",
            "reference",
            selection.Collection,
            resolution.Symbol.ReferenceModId,
            resolution.Symbol.DisplayName,
            resolution.Symbol.Version,
            resolution.Symbol.License);
        return new SourceSnippetResolutionResult(resolution, snippet);
    }

    public async Task<IReadOnlyList<ReferenceDocumentQueryResult>> GetDocumentsAsync(
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null) return [];
        return await MapDocumentsAsync(
            selection,
            await _repository.GetCompletedReferenceDocumentsAsync(selection.Run.IndexId, cancellationToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReferenceDocumentQueryResult>> SearchDocumentsAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateLimit(options.Limit);
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null) return [];
        return await MapDocumentsAsync(
            selection,
            await _repository.SearchCompletedReferenceDocumentsAsync(selection.Run.IndexId, query, options.Limit, cancellationToken),
            cancellationToken);
    }

    public Task<RelationshipQuerySetResult> RefsAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipAsync(selector, options, RelationshipMode.Refs, cancellationToken);

    public Task<RelationshipQuerySetResult> CallersAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipAsync(selector, options, RelationshipMode.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipAsync(selector, options, RelationshipMode.Callees, cancellationToken);

    public async Task<IReadOnlyList<ReferenceModQueryResult>> GetModsAsync(
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null) return [];
        return (await _repository.GetCompletedReferenceModsAsync(selection.Run.IndexId, cancellationToken))
            .OrderBy(mod => mod.ModId, StringComparer.Ordinal)
            .Select(mod => new ReferenceModQueryResult(mod.ModId, mod.DisplayName, mod.Version, mod.License, mod.RootPath, mod.ContentSha256, selection.Collection))
            .ToArray();
    }

    internal async Task<IndexSelection> RequireSelectionForFederationAsync(IndexQueryOptions options, CancellationToken cancellationToken) =>
        await RequireSelectionAsync(options, cancellationToken) ?? throw new InvalidOperationException("No completed reference collection is available.");

    private async Task<RelationshipQuerySetResult> RelationshipAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null)
            return new RelationshipQuerySetResult(NoCompletedIndex(), [], null, mode == RelationshipMode.Callers, "no completed reference collection");

        var resolution = await ResolveInIndexAsync(selection, selector, cancellationToken);
        var selectedIsGame = false;
        if (resolution.Status == SymbolResolutionStatus.NotFound)
        {
            var game = await ResolveAsync(selector, options, cancellationToken);
            if (game.Status != SymbolResolutionStatus.Resolved || game.Symbol is null)
                return new RelationshipQuerySetResult(game, [], null, mode == RelationshipMode.Callers, string.Empty);
            resolution = game;
            selectedIsGame = true;
        }
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new RelationshipQuerySetResult(resolution, [], null, mode == RelationshipMode.Callers, string.Empty);

        var id = resolution.Symbol.SymbolId;
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> edges;
        if (mode == RelationshipMode.Callees && selectedIsGame)
            edges = [];
        else if (mode == RelationshipMode.Callers || (mode == RelationshipMode.Refs && selectedIsGame))
            edges = (await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(selection.Run.IndexId, id, cancellationToken))
                .Select(edge => (edge, "Incoming"))
                .ToArray();
        else if (mode == RelationshipMode.Refs)
            edges = (await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(selection.Run.IndexId, id, cancellationToken))
                .Select(edge => (edge, "Outgoing"))
                .Concat((await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(selection.Run.IndexId, id, cancellationToken)).Select(edge => (edge, "Incoming")))
                .GroupBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        else
            edges = (await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(selection.Run.IndexId, id, cancellationToken))
                .Select(edge => (edge, "Outgoing"))
                .ToArray();

        var selectedRecord = selectedIsGame
            ? await _repository.GetCompletedSymbolByIdAsync(selection.Context.GameIndexId, id, cancellationToken)
            : await _repository.GetCompletedSymbolByIdAsync(selection.Run.IndexId, id, cancellationToken);
        BodyRecoveryStatus? bodyStatus = selectedRecord is not null && IsCallable(selectedRecord.Kind)
            ? selectedRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        var relationships = await MapRelationshipsAsync(selection, edges, cancellationToken);
        return new RelationshipQuerySetResult(
            resolution,
            relationships,
            bodyStatus,
            mode == RelationshipMode.Callers,
            mode == RelationshipMode.Refs ? string.Empty : "Reference relationships are limited to persisted target resolutions.");
    }

    private async Task<SymbolResolutionResult> ResolveInIndexAsync(IndexSelection selection, string selector, CancellationToken cancellationToken)
    {
        var result = await _symbolResolver.ResolveAsync(selection.Run.IndexId, selector, CodebaseKind.ReferenceMod, CodeChannel.Installed, cancellationToken);
        return new SymbolResolutionResult(
            result.Status,
            result.Symbol is null ? null : DecorateReferenceQuerySymbol(selection, result.Symbol),
            result.Candidates.Select(candidate => DecorateReferenceQuerySymbol(selection, candidate)).ToArray());
    }

    private async Task<IReadOnlyList<RelationshipQueryResult>> MapRelationshipsAsync(
        IndexSelection selection,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> edges,
        CancellationToken cancellationToken)
    {
        var ids = edges.SelectMany(item => new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId })
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var referenceSymbols = await _repository.GetCompletedSymbolsByIdsAsync(selection.Run.IndexId, ids, cancellationToken);
        var gameSymbols = await _repository.GetCompletedSymbolsByIdsAsync(selection.Context.GameIndexId, ids, cancellationToken);
        var referenceById = referenceSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var gameById = gameSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        return edges
            .OrderBy(item => item.Edge.RelationshipId, StringComparer.Ordinal)
            .Select(item => new RelationshipQueryResult(
                item.Edge.RelationshipId,
                item.Edge.Kind,
                item.Edge.Evidence,
                item.Direction,
                MapEndpoint(item.Edge.SourceSymbolId, null, referenceById, gameById, selection),
                MapEndpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, referenceById, gameById, selection)))
            .ToArray();
    }

    private RelationshipEndpointQueryResult MapEndpoint(
        string? symbolId,
        string? rawText,
        IReadOnlyDictionary<string, IndexSymbolRecord> referenceById,
        IReadOnlyDictionary<string, IndexSymbolRecord> gameById,
        IndexSelection selection)
    {
        if (symbolId is not null && referenceById.TryGetValue(symbolId, out var reference))
        {
            var symbol = DecorateReferenceSymbol(selection, reference);
            return new RelationshipEndpointQueryResult(symbol.SymbolId, symbol.QualifiedName, symbol.Signature, null, true, symbol.Origin, symbol.Collection, symbol.ReferenceModId, symbol.DisplayName, symbol.Version, symbol.License, symbol.RelativePath, symbol.Sha256);
        }
        if (symbolId is not null && gameById.TryGetValue(symbolId, out var game))
        {
            var symbol = SymbolResolver.ToQueryResult(selection.Context.GameIndexId, CodebaseKind.ScheduleI, CodeChannel.Installed, game, "game");
            return new RelationshipEndpointQueryResult(symbol.SymbolId, symbol.QualifiedName, symbol.Signature, null, true, symbol.Origin, null, null, null, null, null, null, null);
        }
        return new RelationshipEndpointQueryResult(symbolId, null, null, rawText, false, null, null, null, null, null, null, null, null);
    }

    private async Task<IReadOnlyList<ReferenceDocumentQueryResult>> MapDocumentsAsync(
        IndexSelection selection,
        IReadOnlyList<IndexReferenceDocumentRecord> documents,
        CancellationToken cancellationToken)
    {
        var mods = (await _repository.GetCompletedReferenceModsAsync(selection.Run.IndexId, cancellationToken))
            .ToDictionary(mod => mod.ModId, StringComparer.Ordinal);
        return documents
            .OrderBy(document => document.ModId, StringComparer.Ordinal)
            .ThenBy(document => document.RelativePath, StringComparer.Ordinal)
            .Select(document =>
            {
                if (!mods.TryGetValue(document.ModId, out var mod))
                    throw new InvalidDataException("A reference document has no persisted mod owner.");
                var bytes = Encoding.UTF8.GetBytes(document.Content);
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, document.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The reference document hash does not match the recorded SHA-256 for '{document.RelativePath}'.");
                var content = document.Content.Length <= MaxDocumentExcerptCharacters
                    ? document.Content
                    : document.Content[..MaxDocumentExcerptCharacters];
                return new ReferenceDocumentQueryResult(document.ModId, document.RelativePath, document.Kind, document.Sha256, document.ByteCount, content, selection.Collection, mod.DisplayName, mod.Version, mod.License);
            })
            .ToArray();
    }

    private async Task<IndexSelection?> RequireSelectionAsync(IndexQueryOptions options, CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game)
            throw new ArgumentException("Reference queries require Reference or All scope.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ReferenceCollection))
            throw new ArgumentException("Reference queries require an explicit collection.", nameof(options));

        var collection = options.ReferenceCollection.Trim();
        var run = await _repository.GetLatestCompletedReferenceIndexAsync(collection, cancellationToken);
        if (run is null) return null;
        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        var context = await _repository.GetReferenceIndexContextAsync(run.IndexId, cancellationToken);
        if (snapshot is null || snapshot.Codebase != CodebaseKind.ReferenceMod || snapshot.Channel != CodeChannel.Installed || context is null)
            return null;
        var gameRun = await _repository.GetCompletedIndexAsync(context.GameIndexId, cancellationToken);
        if (gameRun is null) return null;
        var gameSnapshot = await _repository.GetCodeSnapshotAsync(gameRun.SnapshotId, cancellationToken);
        if (gameSnapshot is null || gameSnapshot.Codebase != CodebaseKind.ScheduleI || gameSnapshot.Channel != CodeChannel.Installed)
            return null;
        var mods = await _repository.GetCompletedReferenceModsAsync(run.IndexId, cancellationToken);
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(run.IndexId, cancellationToken);
        return new IndexSelection(collection, run, context, gameRun, mods, sourceFiles);
    }

    private SymbolQueryResult DecorateReferenceSymbol(IndexSelection selection, IndexSymbolRecord symbol)
    {
        var result = SymbolResolver.ToQueryResult(selection.Run.IndexId, CodebaseKind.ReferenceMod, CodeChannel.Installed, symbol, "reference", selection.Collection);
        return DecorateReferenceQuerySymbol(selection, result);
    }

    private SymbolQueryResult DecorateReferenceQuerySymbol(IndexSelection selection, SymbolQueryResult result)
    {
        var mod = selection.Mods.FirstOrDefault(candidate => candidate.SymbolIds.Contains(result.SymbolId, StringComparer.Ordinal));
        return result with
        {
            Origin = "reference",
            Collection = selection.Collection,
            ReferenceModId = mod?.ModId,
            DisplayName = mod?.DisplayName,
            Version = mod?.Version,
            License = mod?.License,
            RelativePath = mod is null
                ? null
                : selection.SourceFiles
                    .Where(file => file.RelativePath.StartsWith(mod.ModId + "/", StringComparison.Ordinal))
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .Select(file => file.RelativePath)
                    .FirstOrDefault(),
            Sha256 = mod is null
                ? null
                : selection.SourceFiles
                    .Where(file => file.RelativePath.StartsWith(mod.ModId + "/", StringComparison.Ordinal))
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .Select(file => file.Sha256)
                    .FirstOrDefault()
        };
    }

    private static SymbolResolutionResult DecorateGameResolution(SymbolResolutionResult result, string collection) =>
        new(
            result.Status,
            result.Symbol is null ? null : result.Symbol with { Origin = "game", Collection = null },
            result.Candidates.Select(candidate => candidate with { Origin = "game", Collection = null }).ToArray());

    private static SymbolResolutionResult NoCompletedIndex() => new(SymbolResolutionStatus.NoCompletedIndex, null, []);

    private static int Rank(SymbolQueryResult result, string query)
    {
        if (string.Equals(result.QualifiedName, query, StringComparison.OrdinalIgnoreCase) || string.Equals(result.Signature, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (result.QualifiedName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (result.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (result.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static void ValidateLimit(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static bool IsCallable(string kind) => kind is "Method" or "Constructor";

    private static string ResolveReferenceIndexRoot(string dataRoot, string indexId)
    {
        var root = Path.GetFullPath(Path.Combine(dataRoot, "reference", indexId));
        if (!Directory.Exists(root)) throw new FileNotFoundException("The completed reference index source root was not found.", root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The reference index source root is a reparse point.");
        return root;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The indexed reference source path is not a safe relative path.");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)
            ? fullPath
            : throw new InvalidDataException("The indexed reference source path escaped its owned index root.");
    }

    private enum RelationshipMode { Refs, Callers, Callees }

    internal sealed record IndexSelection(
        string Collection,
        IndexRunRecord Run,
        ReferenceIndexContextRecord Context,
        IndexRunRecord GameRun,
        IReadOnlyList<IndexReferenceModRecord> Mods,
        IReadOnlyList<IndexSourceFileRecord> SourceFiles);
}
