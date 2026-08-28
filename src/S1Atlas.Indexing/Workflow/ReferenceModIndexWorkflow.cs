using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Relationships;
using S1Atlas.Indexing.Source;

namespace S1Atlas.Indexing.Workflow;

public sealed class ReferenceModIndexWorkflow
{
    private readonly IIndexRepository _repository;
    private readonly ReferenceModFileSelector _selector;
    private readonly ReferenceModInputHasher _hasher;
    private readonly ReferenceModIndexSource _source;
    private readonly ReferenceGameSymbolLoader _gameSymbols;
    private readonly GeneratedSourceWriter _sourceWriter = new();
    private readonly SymbolFingerprintService _fingerprints = new();
    private readonly ReferenceRelationshipResolver _relationships = new();
    private readonly string _dataRoot;

    public ReferenceModIndexWorkflow(
        string dataRoot,
        IIndexRepository repository,
        ReferenceModFileSelector selector,
        ReferenceModInputHasher hasher,
        ReferenceModIndexSource source,
        ReferenceGameSymbolLoader gameSymbols)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _gameSymbols = gameSymbols ?? throw new ArgumentNullException(nameof(gameSymbols));
    }

    public static string CreateIndexId(
        string gameIndexId,
        string verifiedExtractionIdentity,
        string normalizedCollectionHash,
        string settings,
        int schemaVersion) => IndexingWorkflow.CreateIndexId(
            gameIndexId + "\n" + verifiedExtractionIdentity + "\n" + normalizedCollectionHash,
            IndexingWorkflow.DecompilerPackage,
            IndexingWorkflow.DecompilerVersion,
            settings,
            schemaVersion);

    public async Task<IndexingWorkflowResult> RunAsync(
        string buildId,
        ReferenceCollectionDefinition collection,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentNullException.ThrowIfNull(collection);
        if (!string.Equals(buildId, collection.BuildId, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested build does not match the reference collection.");

        var game = await _gameSymbols.LoadAsync(collection.GameIndexId, cancellationToken);
        var selected = _selector.Select(collection.Mods);
        var initialHash = await _hasher.HashAsync(selected, cancellationToken);
        var collectionHash = CreateCollectionHash(collection, initialHash.CollectionContentSha256);
        var settings = force ? "reference:forced:" + Guid.NewGuid().ToString("N") : "reference";
        var indexId = CreateIndexId(game.IndexId, game.VerifiedExtractionIdentity, collectionHash, settings, IndexingWorkflow.IndexSchemaVersion);
        var snapshotId = "reference:" + game.IndexId + ":" + indexId;

        if (!force)
        {
            var existing = await _repository.GetCompletedIndexAsync(indexId, cancellationToken);
            if (existing is not null)
            {
                var symbols = await _repository.GetCompletedSymbolsAsync(indexId, cancellationToken);
                var sourceFiles = await _repository.GetCompletedSourceFilesAsync(indexId, cancellationToken);
                var relationships = await _repository.GetCompletedRelationshipsAsync(indexId, cancellationToken);
                var mods = await _repository.GetCompletedReferenceModsAsync(indexId, cancellationToken);
                var documents = await _repository.GetCompletedReferenceDocumentsAsync(indexId, cancellationToken);
                return new IndexingWorkflowResult(indexId, snapshotId, true, symbols.Count, sourceFiles.Count, relationships.Count, [], 0, mods.Count, documents.Count, symbols.Count);
            }
        }

        var paths = OwnedIndexPaths.ForReferenceMod(_dataRoot, indexId);
        var snapshot = await _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken);
        if (snapshot is null)
        {
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(snapshotId, CodebaseKind.ReferenceMod, CodeChannel.Installed, collectionHash, DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken);
        }

        Directory.CreateDirectory(paths.StagingRoot);
        var started = false;
        var databaseCompleted = false;
        try
        {
            await _repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O")), cancellationToken);
            started = true;
            var indexed = await ReadSelectedInputsAsync(selected, initialHash.Files, snapshotId, paths.StagingRoot, cancellationToken);
            var postReadHash = await _hasher.HashAsync(selected, cancellationToken);
            if (!string.Equals(initialHash.CollectionContentSha256, postReadHash.CollectionContentSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Reference mod inputs changed during indexing.");

            var relationshipLookup = BuildRelationshipLookup(game.Symbols, indexed.Symbols);
            var relationships = _relationships.Resolve(indexed.Decompilations, relationshipLookup);
            var mods = BuildReferenceMods(collection, initialHash.Files, indexed.Symbols);
            var fingerprints = _fingerprints.Create(indexed.Symbols);
            await _repository.CompleteIndexRunAsync(
                indexId,
                new IndexWriteSet(
                    indexed.Symbols,
                    indexed.SourceFiles,
                    [],
                    fingerprints,
                    relationships,
                    null,
                    new ReferenceIndexContextRecord(indexId, game.IndexId, buildId),
                    mods,
                    indexed.Documents),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
            databaseCompleted = true;
            if (Directory.Exists(paths.FinalRoot))
                Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, cancellationToken);
            return new IndexingWorkflowResult(indexId, snapshotId, false, indexed.Symbols.Count, indexed.SourceFiles.Count, relationships.Count, [], 0, mods.Count, indexed.Documents.Count, indexed.Symbols.Count);
        }
        catch (Exception exception)
        {
            if (started && !databaseCompleted)
            {
                try { await _repository.FailIndexRunAsync(indexId, exception.Message, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None); } catch { }
            }
            if (Directory.Exists(paths.StagingRoot))
                Directory.Delete(paths.StagingRoot, recursive: true);
            throw;
        }
    }

    private async Task<IndexedReferenceInputs> ReadSelectedInputsAsync(
        IReadOnlyList<ReferenceModInputFile> selected,
        IReadOnlyList<ReferenceModInputFileHash> hashes,
        string snapshotId,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var filesByPath = hashes.ToDictionary(file => (file.ModId, file.RelativePath));
        var symbols = new List<IndexSymbolRecord>();
        var sourceFiles = new List<IndexSourceFileRecord>();
        var documents = new List<IndexReferenceDocumentRecord>();
        var decompilations = new List<ReferenceModDecompilation>();
        foreach (var input in selected)
        {
            if (!filesByPath.TryGetValue((input.ModId, input.RelativePath), out var hash))
                throw new InvalidOperationException("Selected reference input was not hashed.");
            if (input.Kind == ReferenceModInputKind.ManagedAssembly)
            {
                var decompilation = await _source.ReadModAssemblyAsync(input, cancellationToken);
                var generatedPath = input.ModId + "/" + Path.ChangeExtension(input.RelativePath, ".cs")!.Replace('\\', '/');
                sourceFiles.Add(await _sourceWriter.WriteAsync(stagingRoot, generatedPath, decompilation.SourceText, snapshotId, cancellationToken));
                symbols.AddRange(BuildSymbols(input.ModId, decompilation, snapshotId));
                decompilations.Add(new ReferenceModDecompilation(input.ModId, decompilation));
            }
            else
            {
                documents.Add(await _source.ReadDocumentAsync(hash, cancellationToken));
            }
        }

        return new IndexedReferenceInputs(
            symbols.GroupBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal).Select(group => group.First()).OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal).ToArray(),
            sourceFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            documents.OrderBy(document => document.ModId, StringComparer.Ordinal).ThenBy(document => document.RelativePath, StringComparer.Ordinal).ToArray(),
            decompilations);
    }

    private static IReadOnlyList<IndexSymbolRecord> BuildSymbols(string modId, ManagedDecompilation decompilation, string snapshotId)
    {
        var symbols = new List<IndexSymbolRecord>();
        foreach (var type in decompilation.Types)
        {
            AddSymbol(SymbolKind.Type, type.FullName, type.FullName, null, false);
            foreach (var member in type.Members)
            {
                var kind = member.Kind switch
                {
                    ManagedMemberKind.Constructor => SymbolKind.Constructor,
                    ManagedMemberKind.Method => SymbolKind.Method,
                    ManagedMemberKind.Field => SymbolKind.Field,
                    ManagedMemberKind.Property => SymbolKind.Property,
                    ManagedMemberKind.Event => SymbolKind.Event,
                    _ => throw new ArgumentOutOfRangeException()
                };
                AddSymbol(kind, ManagedMemberIdentity.Render(type.FullName, member), ManagedMemberIdentity.Render(type.FullName, member), member.Kind is ManagedMemberKind.Method or ManagedMemberKind.Constructor ? member.BodyRecoveryStatus : null, member.IsPublic);
            }
        }
        return symbols;

        void AddSymbol(SymbolKind kind, string qualifiedName, string signature, BodyRecoveryStatus? recovery, bool isPublic)
        {
            var prefixedName = modId + "/" + qualifiedName;
            var key = SymbolIdentity.Create(CodebaseKind.ReferenceMod, CodeChannel.Installed, kind, prefixedName).CanonicalKey;
            symbols.Add(new IndexSymbolRecord(IndexingWorkflow.HashId(snapshotId + "\n" + key), snapshotId, key, kind.ToString(), prefixedName, signature, false, recovery, isPublic));
        }
    }

    private static IReadOnlyDictionary<(string Origin, string Type, string Name, int Arity, string Signature), IndexSymbolRecord> BuildRelationshipLookup(
        IReadOnlyList<IndexSymbolRecord> gameSymbols,
        IReadOnlyList<IndexSymbolRecord> referenceSymbols)
    {
        var entries = gameSymbols.Select(symbol => (Origin: "game", Symbol: symbol))
            .Concat(referenceSymbols.Select(symbol => (Origin: ExtractModId(symbol.QualifiedName), Symbol: symbol)));
        return entries.ToDictionary(entry => ReferenceRelationshipResolver.CreateLookupKey(entry.Origin, entry.Symbol.Signature), entry => entry.Symbol);
    }

    private static IReadOnlyList<IndexReferenceModRecord> BuildReferenceMods(
        ReferenceCollectionDefinition collection,
        IReadOnlyList<ReferenceModInputFileHash> inputs,
        IReadOnlyList<IndexSymbolRecord> symbols)
    {
        var definitions = collection.Mods.ToDictionary(mod => mod.ModId, StringComparer.Ordinal);
        return inputs.GroupBy(input => input.ModId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var definition = definitions[group.Key];
                var contentHash = IndexingWorkflow.HashId(string.Join("\n", group.OrderBy(file => file.RelativePath, StringComparer.Ordinal).Select(file => file.RelativePath + "\n" + file.Sha256)));
                var ownedSymbols = symbols.Where(symbol => string.Equals(ExtractModId(symbol.QualifiedName), group.Key, StringComparison.Ordinal)).Select(symbol => symbol.SymbolId).Order(StringComparer.Ordinal).ToArray();
                return new IndexReferenceModRecord(group.Key, definition.DisplayName, definition.Version, definition.License, definition.RootPath, contentHash, ownedSymbols);
            })
            .ToArray();
    }

    private static string ExtractModId(string qualifiedName)
    {
        var separator = qualifiedName.IndexOf('/');
        return separator > 0 ? qualifiedName[..separator] : throw new InvalidOperationException("Reference symbol lacks a mod identity prefix.");
    }

    public static string CreateCollectionHash(ReferenceCollectionDefinition collection, string inputHash)
    {
        var values = new List<string> { collection.CollectionId, collection.CollectionName ?? string.Empty, collection.BuildId, collection.GameIndexId, inputHash };
        foreach (var mod in collection.Mods.OrderBy(mod => mod.ModId, StringComparer.Ordinal))
        {
            values.Add(mod.ModId);
            values.Add(mod.DisplayName);
            values.Add(mod.Version);
            values.Add(mod.License ?? string.Empty);
            values.Add(mod.ContentSha256);
            values.AddRange(mod.Include.Order(StringComparer.Ordinal));
            values.AddRange(mod.Exclude.Order(StringComparer.Ordinal));
        }
        return IndexingWorkflow.HashId(string.Join("\n", values));
    }

    private sealed record IndexedReferenceInputs(
        IReadOnlyList<IndexSymbolRecord> Symbols,
        IReadOnlyList<IndexSourceFileRecord> SourceFiles,
        IReadOnlyList<IndexReferenceDocumentRecord> Documents,
        IReadOnlyList<ReferenceModDecompilation> Decompilations);
}
