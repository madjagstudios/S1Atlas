using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Paths;

namespace S1Atlas.Indexing.Query;

public sealed class ReferenceModQueryService
{
    public const int MaxDocumentExcerptCharacters = 16 * 1024;
    private const int MaxSourceNeighborhoodLimit = 50;
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
        CancellationToken cancellationToken,
        string? referenceIndexId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var selection = await RequireSelectionAsync(options, referenceIndexId, cancellationToken);
        if (selection is null)
            return NoCompletedIndex();

        var reference = await ResolveInIndexAsync(selection, selector, cancellationToken);
        return reference;
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
        CancellationToken cancellationToken,
        bool fullType = false,
        int relatedLimit = 10,
        string? referenceIndexId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0) throw new ArgumentOutOfRangeException(nameof(context));
        ValidateSourceRelatedLimit(relatedLimit);
        var selection = await RequireSelectionAsync(options, referenceIndexId, cancellationToken);
        if (selection is null) return new SourceSnippetResolutionResult(NoCompletedIndex(), null);

        var resolution = await ResolveInIndexAsync(selection, selector, cancellationToken);
        if (resolution.Status == SymbolResolutionStatus.NotFound)
            return new SourceSnippetResolutionResult(resolution, null);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new SourceSnippetResolutionResult(resolution, null);

        var symbol = await _repository.GetCompletedSymbolByIdAsync(selection.Run.IndexId, resolution.Symbol.SymbolId, cancellationToken)
            ?? throw new InvalidDataException("The resolved reference symbol disappeared from the completed index.");
        if (fullType && !string.Equals(symbol.Kind, "Type", StringComparison.Ordinal))
        {
            var containingType = await ResolveContainingTypeAsync(selection, symbol, cancellationToken);
            if (containingType is null)
                return new SourceSnippetResolutionResult(new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []), null);

            symbol = containingType.Value.Record;
            resolution = new SymbolResolutionResult(SymbolResolutionStatus.Resolved, containingType.Value.Symbol, []);
            relatedLimit = 0;
        }
        var locations = (await _repository.GetCompletedSourceLocationsAsync(selection.Run.IndexId, cancellationToken))
            .Where(location => string.Equals(location.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
            .OrderBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ToArray();
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(selection.Run.IndexId, cancellationToken);
        if (locations.Length == 0)
            return new SourceSnippetResolutionResult(resolution, null);
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
        var selectedSpan = context == 0
            ? read.Text
            : (await _sourceSnippetReader.ReadAsync(sourcePath, sourceFile.Sha256, location, 0, cancellationToken)).Text;
        BodyRecoveryStatus? bodyStatus = IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : null;
        var queryLocation = new SourceLocationQueryResult(location.SymbolId, location.StartLine, location.StartColumn, location.EndLine, location.EndColumn);
        var selectedQuerySymbol = resolution.Symbol ?? throw new InvalidDataException("The resolved reference symbol metadata is missing.");
        var runtimeVerification = RuntimeVerificationClassifier.Classify(selectedSpan, selectedQuerySymbol.Signature);
        RelationshipEvidenceQueryResult? neighborhood = null;
        string? neighborhoodNotice = null;
        if (!fullType && relatedLimit > 0 && IsCallable(symbol.Kind))
        {
            try
            {
                neighborhood = await GetRelationshipEvidenceForSelectionAsync(selection, symbol, relatedLimit, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                neighborhoodNotice = "Relationship neighborhood evidence was unavailable.";
            }
        }
        var snippet = new SourceSnippetQueryResult(
            selectedQuerySymbol,
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
            selectedQuerySymbol.ReferenceModId,
            selectedQuerySymbol.DisplayName,
            selectedQuerySymbol.Version,
            selectedQuerySymbol.License,
            RuntimeVerification: runtimeVerification,
            Neighborhood: neighborhood,
            NeighborhoodNotice: neighborhoodNotice);
        return new SourceSnippetResolutionResult(resolution, snippet);
    }

    public async Task<RelationshipEvidenceQueryResult> GetRelationshipEvidenceAsync(
        string selector,
        IndexQueryOptions options,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateSourceNeighborhoodLimit(relatedLimit);

        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null)
        {
            return new RelationshipEvidenceQueryResult(
                [],
                0,
                [],
                0,
                [],
                0,
                "no completed reference collection",
                "no completed reference collection");
        }

        var resolution = await ResolveInIndexAsync(selection, selector, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
        {
            return new RelationshipEvidenceQueryResult(
                [],
                0,
                [],
                0,
                [],
                0,
                "symbol not found in this index",
                "symbol not found in this index");
        }

        var symbol = await _repository.GetCompletedSymbolByIdAsync(
            selection.Run.IndexId,
            resolution.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved reference symbol disappeared from the completed index.");
        return await GetRelationshipEvidenceForSelectionAsync(selection, symbol, relatedLimit, cancellationToken);
    }

    public async Task<IReadOnlyList<ReferenceDocumentQueryResult>> GetDocumentsAsync(
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ValidateLimit(options.Limit);
        var selection = await RequireSelectionAsync(options, cancellationToken);
        if (selection is null) return [];
        return await MapDocumentsAsync(
            selection,
            await _repository.GetCompletedReferenceDocumentsAsync(selection.Run.IndexId, options.Limit, cancellationToken),
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

    public async Task<ReferenceCollectionListResult> ListCollectionsAsync(
        CancellationToken cancellationToken)
    {
        var collections = new List<ReferenceCollectionQueryResult>();
        foreach (var run in await _repository.GetCompletedReferenceIndexesAsync(cancellationToken))
        {
            var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
            var context = await _repository.GetReferenceIndexContextAsync(run.IndexId, cancellationToken);
            if (snapshot is null ||
                context is null ||
                snapshot.Codebase != CodebaseKind.ReferenceMod ||
                snapshot.Channel != CodeChannel.Installed)
            {
                continue;
            }

            var mods = (await _repository.GetCompletedReferenceModsAsync(run.IndexId, cancellationToken))
                .OrderBy(mod => mod.ModId, StringComparer.Ordinal)
                .Select(mod => new ReferenceCollectionModQueryResult(
                    mod.ModId,
                    mod.DisplayName,
                    mod.Version,
                    mod.License,
                    mod.ContentSha256))
                .ToArray();
            collections.Add(new ReferenceCollectionQueryResult(
                snapshot.SourceIdentity,
                run.IndexId,
                run.SnapshotId,
                context.BuildId,
                context.GameIndexId,
                mods.Length,
                mods));
        }

        // GetCompletedReferenceIndexesAsync returns each collection in completion order.
        // Preserve that repository ordering so catalog selection matches query-by-name.
        var unique = collections
            .GroupBy(collection => collection.Collection, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(collection => collection.Collection, StringComparer.Ordinal)
            .ToArray();
        return new ReferenceCollectionListResult(unique.Length, unique);
    }

    public async Task<ReferenceCollectionAuthorityQueryResult?> GetCollectionAuthorityAsync(
        string collection,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var selection = await RequireSelectionAsync(
            new IndexQueryOptions(
                CodebaseKind.ReferenceMod,
                CodeChannel.Installed,
                Scope: IndexQueryScope.Reference,
                ReferenceCollection: collection),
            cancellationToken);
        return selection is null
            ? null
            : new ReferenceCollectionAuthorityQueryResult(
                selection.Collection,
                selection.Run.IndexId,
                selection.Context.BuildId,
                selection.Context.GameIndexId);
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
        if (resolution.Status == SymbolResolutionStatus.NotFound && options.Scope == IndexQueryScope.All)
        {
            var gameRun = await _repository.GetCompletedIndexAsync(selection.Context.GameIndexId, cancellationToken);
            if (gameRun is not null)
            {
                var game = await _symbolResolver.ResolveAsync(
                    gameRun.IndexId,
                    selector,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    cancellationToken);
                if (game.Status == SymbolResolutionStatus.Resolved && game.Symbol is not null)
                {
                    resolution = DecorateGameResolution(game);
                    selectedIsGame = true;
                }
            }
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
        var relationships = await MapRelationshipsAsync(selection, edges, options.Scope == IndexQueryScope.All, cancellationToken);
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
        bool includeGameEndpoints,
        CancellationToken cancellationToken)
    {
        var ids = edges.SelectMany(item => new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId })
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var referenceSymbols = await _repository.GetCompletedSymbolsByIdsAsync(selection.Run.IndexId, ids, cancellationToken);
        var gameSymbols = includeGameEndpoints
            ? await _repository.GetCompletedSymbolsByIdsAsync(selection.Context.GameIndexId, ids, cancellationToken)
            : [];
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

    private async Task<RelationshipEvidenceQueryResult> GetRelationshipEvidenceForSelectionAsync(
        IndexSelection selection,
        IndexSymbolRecord symbol,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        var callers = (await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selection.Run.IndexId,
                symbol.SymbolId,
                cancellationToken))
            .Where(edge => IsCallLike(edge.Kind))
            .Select(edge => (edge, Direction: "Incoming"))
            .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        var callees = (await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
                selection.Run.IndexId,
                symbol.SymbolId,
                cancellationToken))
            .Where(edge => IsCallLike(edge.Kind))
            .Select(edge => (edge, Direction: "Outgoing"))
            .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        var mapped = await MapRelationshipsAsync(
            selection,
            callers.Concat(callees).DistinctBy(item => (item.edge.RelationshipId, item.Direction)).ToArray(),
            includeGameEndpoints: true,
            cancellationToken);
        var callerKeys = callers.Select(item => (item.edge.RelationshipId, item.Direction)).ToHashSet();
        var calleeKeys = callees.Select(item => (item.edge.RelationshipId, item.Direction)).ToHashSet();
        var bodyStatus = IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : (BodyRecoveryStatus?)null;

        return new RelationshipEvidenceQueryResult(
            [],
            0,
            mapped.Where(item => callerKeys.Contains((item.RelationshipId, item.Direction))).Take(relatedLimit).ToArray(),
            callers.Length,
            mapped.Where(item => calleeKeys.Contains((item.RelationshipId, item.Direction))).Take(relatedLimit).ToArray(),
            callees.Length,
            CompletenessNotice(bodyStatus, callers: true),
            CompletenessNotice(bodyStatus, callers: false));
    }

    private async Task<(IndexSymbolRecord Record, SymbolQueryResult Symbol)?> ResolveContainingTypeAsync(
        IndexSelection selection,
        IndexSymbolRecord symbol,
        CancellationToken cancellationToken)
    {
        var parts = symbol.CanonicalKey.Split(':', 4, StringSplitOptions.None);
        if (parts.Length < 4)
            return null;

        var memberKey = parts[3];
        var separator = memberKey.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        var typeKey = $"{parts[0]}:{parts[1]}:Type:{memberKey[..separator]}";
        var records = await _repository.GetCompletedSymbolByCanonicalKeyAsync(
            selection.Run.IndexId,
            typeKey,
            cancellationToken);
        if (records.Count != 1)
            return null;

        var record = records[0];
        return (record, DecorateReferenceQuerySymbol(
            selection,
            SymbolResolver.ToQueryResult(
                selection.Run.IndexId,
                CodebaseKind.ReferenceMod,
                CodeChannel.Installed,
                record)));
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

    private Task<IndexSelection?> RequireSelectionAsync(IndexQueryOptions options, CancellationToken cancellationToken) =>
        RequireSelectionAsync(options, null, cancellationToken);

    private async Task<IndexSelection?> RequireSelectionAsync(
        IndexQueryOptions options,
        string? referenceIndexId,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game)
            throw new ArgumentException("Reference queries require Reference or All scope.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ReferenceCollection))
            throw new ArgumentException("Reference queries require an explicit collection.", nameof(options));

        var collection = options.ReferenceCollection.Trim();
        var run = string.IsNullOrWhiteSpace(referenceIndexId)
            ? await _repository.GetLatestCompletedReferenceIndexAsync(collection, cancellationToken)
            : await _repository.GetCompletedIndexAsync(referenceIndexId, cancellationToken);
        if (run is null) return null;
        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        var context = await _repository.GetReferenceIndexContextAsync(run.IndexId, cancellationToken);
        if (snapshot is null ||
            snapshot.Codebase != CodebaseKind.ReferenceMod ||
            snapshot.Channel != CodeChannel.Installed ||
            (!string.Equals(run.IndexId, collection, StringComparison.Ordinal) &&
             !string.Equals(snapshot.SourceIdentity, collection, StringComparison.Ordinal)) ||
            context is null)
            return null;
        var gameRun = await _repository.GetCompletedIndexAsync(context.GameIndexId, cancellationToken);
        if (gameRun is null) return null;
        var gameSnapshot = await _repository.GetCodeSnapshotAsync(gameRun.SnapshotId, cancellationToken);
        if (gameSnapshot is null || gameSnapshot.Codebase != CodebaseKind.ScheduleI || gameSnapshot.Channel != CodeChannel.Installed)
            return null;
        var mods = await _repository.GetCompletedReferenceModsAsync(run.IndexId, cancellationToken);
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(run.IndexId, cancellationToken);
        var sourceLocations = await _repository.GetCompletedSourceLocationsAsync(run.IndexId, cancellationToken);
        return new IndexSelection(snapshot.SourceIdentity, run, context, gameRun, mods, sourceFiles, sourceLocations);
    }

    private SymbolQueryResult DecorateReferenceSymbol(IndexSelection selection, IndexSymbolRecord symbol)
    {
        var result = SymbolResolver.ToQueryResult(selection.Run.IndexId, CodebaseKind.ReferenceMod, CodeChannel.Installed, symbol, "reference", selection.Collection);
        return DecorateReferenceQuerySymbol(selection, result);
    }

    private SymbolQueryResult DecorateReferenceQuerySymbol(IndexSelection selection, SymbolQueryResult result)
    {
        var mod = selection.Mods.FirstOrDefault(candidate => candidate.SymbolIds.Contains(result.SymbolId, StringComparer.Ordinal));
        var source = SourceProvenance(selection, result.SymbolId);
        return result with
        {
            Origin = "reference",
            Collection = selection.Collection,
            ReferenceModId = mod?.ModId,
            DisplayName = mod?.DisplayName,
            Version = mod?.Version,
            License = mod?.License,
            RelativePath = source.RelativePath,
            Sha256 = source.Sha256
        };
    }

    private static (string? RelativePath, string? Sha256) SourceProvenance(IndexSelection selection, string symbolId)
    {
        var locations = selection.SourceLocations
            .Where(location => string.Equals(location.SymbolId, symbolId, StringComparison.Ordinal))
            .ToArray();
        if (locations.Length != 1)
            return (null, null);

        var sourceFile = selection.SourceFiles.SingleOrDefault(file =>
            string.Equals(file.SourceFileId, locations[0].SourceFileId, StringComparison.Ordinal));
        return sourceFile is null ? (null, null) : (sourceFile.RelativePath, sourceFile.Sha256);
    }

    private static SymbolResolutionResult DecorateGameResolution(SymbolResolutionResult result) =>
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

    private static void ValidateSourceNeighborhoodLimit(int relatedLimit)
    {
        if (relatedLimit <= 0 || relatedLimit > MaxSourceNeighborhoodLimit)
            throw new ArgumentOutOfRangeException(nameof(relatedLimit), $"The source neighborhood limit must be between 1 and {MaxSourceNeighborhoodLimit}.");
    }

    private static void ValidateSourceRelatedLimit(int relatedLimit)
    {
        if (relatedLimit < 0 || relatedLimit > MaxSourceNeighborhoodLimit)
            throw new ArgumentOutOfRangeException(nameof(relatedLimit), $"The source neighborhood limit must be between 0 and {MaxSourceNeighborhoodLimit}.");
    }

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

    private static bool IsCallLike(string kind) =>
        string.Equals(kind, "Calls", StringComparison.Ordinal) ||
        string.Equals(kind, "Constructs", StringComparison.Ordinal);

    private static bool IsCallable(string kind) => kind is "Method" or "Constructor";

    private static string ResolveReferenceIndexRoot(string dataRoot, string indexId)
    {
        var root = OwnedIndexPaths.ForReferenceMod(dataRoot, indexId).FinalRoot;
        if (!Directory.Exists(root)) throw new FileNotFoundException("The completed reference index source root was not found.", root);
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
        IReadOnlyList<IndexSourceFileRecord> SourceFiles,
        IReadOnlyList<IndexSourceLocationRecord> SourceLocations);

    internal async Task<IndexSelection?> GetSelectionForFederationAsync(
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        await RequireSelectionAsync(options, cancellationToken);

    internal async Task<IndexSelection?> GetSelectionForFederationAsync(
        IndexQueryOptions options,
        string? referenceIndexId,
        CancellationToken cancellationToken) =>
        await RequireSelectionAsync(options, referenceIndexId, cancellationToken);
}
