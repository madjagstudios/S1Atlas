using System.Security.Cryptography;
using System.Text;
using ICSharpCode.Decompiler.CSharp;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Relationships;
using S1Atlas.Indexing.Source;
using S1Atlas.Indexing.Upstream;

namespace S1Atlas.Indexing.Workflow;

public sealed class ApiIndexingWorkflow
{
    private const int IndexSchemaVersion = IndexingWorkflow.IndexSchemaVersion;
    private const string DecompilerPackage = "ICSharpCode.Decompiler";
    private static string DecompilerVersion => typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The ILSpy decompiler assembly has no version.");

    private readonly string _dataRoot;
    private readonly IIndexRepository _repository;
    private readonly InstalledApiIndexSource _source;
    private readonly GeneratedSourceWriter _sourceWriter = new();
    private readonly RoslynSourceIndexer _sourceIndexer = new();
    private readonly SymbolFingerprintService _fingerprints = new();
    private readonly RelationshipExtractor _relationships = new();

    public ApiIndexingWorkflow(
        string dataRoot,
        IIndexRepository repository,
        IManagedDecompiler decompiler)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(decompiler);
        _source = new InstalledApiIndexSource(decompiler);
    }

    public async Task<IndexingWorkflowResult> RunInstalledAsync(
        CodebaseKind codebase,
        string environmentSnapshotId,
        string assemblyPath,
        bool force,
        CancellationToken cancellationToken)
    {
        if (codebase is not (CodebaseKind.S1Api or CodebaseKind.S1MApi))
            throw new ArgumentException("Installed API indexing only supports S1API and S1MAPI.", nameof(codebase));
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
            throw new FileNotFoundException("The installed API assembly was not found.", fullAssemblyPath);

        var binarySha256 = await HashFileAsync(fullAssemblyPath, cancellationToken);
        var identity = codebase + ":installed:" + environmentSnapshotId + ":" + binarySha256;
        var indexId = IndexingWorkflow.CreateIndexId(
            identity,
            DecompilerPackage,
            DecompilerVersion,
            force ? "installed:forced:" + Guid.NewGuid().ToString("N") : "installed",
            IndexSchemaVersion);
        var snapshotId = "api:" + codebase + ":installed:" + environmentSnapshotId + ":" + binarySha256;

        var existingSnapshot = await _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken);
        if (existingSnapshot is null)
        {
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(
                    snapshotId,
                    codebase,
                    CodeChannel.Installed,
                    binarySha256,
                    DateTimeOffset.UtcNow.ToString("O"),
                    environmentSnapshotId),
                cancellationToken);
        }

        if (!force)
        {
            var existing = await _repository.GetCompletedIndexAsync(indexId, cancellationToken);
            if (existing is not null)
            {
                var symbols = await _repository.GetCompletedSymbolsAsync(indexId, cancellationToken);
                var relationships = await _repository.GetCompletedRelationshipsAsync(indexId, cancellationToken);
                var sourceFiles = await _repository.GetCompletedSourceFilesAsync(indexId, cancellationToken);
                return new IndexingWorkflowResult(indexId, snapshotId, true, symbols.Count, sourceFiles.Count, relationships.Count, []);
            }
        }

        var paths = OwnedIndexPaths.ForInstalled(
            _dataRoot,
            CodebaseDirectoryName(codebase),
            binarySha256,
            indexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var run = new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O"));
        var runStarted = false;
        var databaseCompleted = false;
        try
        {
            await _repository.StartIndexRunAsync(run, cancellationToken);
            runStarted = true;
            var decompilation = await _source.ReadAsync(
                new DependencyVersion(
                    codebase == CodebaseKind.S1Api ? DependencyKind.S1Api : DependencyKind.S1Mapi,
                    null,
                    fullAssemblyPath,
                    true,
                    binarySha256),
                cancellationToken);
            var finalSha256 = await HashFileAsync(fullAssemblyPath, cancellationToken);
            if (!string.Equals(binarySha256, finalSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The installed API assembly changed during indexing.");

            var sourceFile = await _sourceWriter.WriteAsync(
                paths.StagingRoot,
                "generated/" + CodebaseDirectoryName(codebase) + ".cs",
                decompilation?.SourceText ?? throw new InvalidDataException("The installed API assembly could not be decompiled."),
                snapshotId,
                cancellationToken);
            var symbols = BuildSymbols(decompilation, snapshotId, codebase);
            var sourceSymbols = _sourceIndexer.Index(
                decompilation.SourceText,
                codebase,
                CodeChannel.Installed,
                sourceFile.RelativePath);
            var sourceLocations = BuildSourceLocations(sourceSymbols, symbols, sourceFile);
            var fingerprints = _fingerprints.Create(
                symbols,
                BuildMethodEvidence(decompilation, symbols, codebase),
                BuildSourceEvidence(sourceSymbols, symbols, decompilation.SourceText));
            var relationships = BuildRelationships(decompilation, symbols, snapshotId, codebase);

            var writtenPath = Path.Combine(paths.StagingRoot, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var writtenHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(writtenPath, cancellationToken))).ToLowerInvariant();
            if (!string.Equals(writtenHash, sourceFile.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Generated source hash validation failed.");

            await _repository.CompleteIndexRunAsync(
                indexId,
                new IndexWriteSet(symbols, [sourceFile], sourceLocations, fingerprints, relationships),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
            databaseCompleted = true;
            if (Directory.Exists(paths.FinalRoot)) Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, cancellationToken);
            return new IndexingWorkflowResult(indexId, snapshotId, false, symbols.Count, 1, relationships.Count, []);
        }
        catch (Exception exception)
        {
            if (runStarted && !databaseCompleted)
            {
                try { await _repository.FailIndexRunAsync(indexId, exception.Message, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None); } catch { }
            }
            if (Directory.Exists(paths.StagingRoot)) Directory.Delete(paths.StagingRoot, recursive: true);
            throw;
        }
    }

    public async Task<IndexingWorkflowResult> RunCachedSourceAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string commitSha,
        bool force,
        CancellationToken cancellationToken)
    {
        if (codebase is not (CodebaseKind.S1Api or CodebaseKind.S1MApi))
            throw new ArgumentException("Cached API indexing only supports S1API and S1MAPI.", nameof(codebase));
        if (channel is not (CodeChannel.Release or CodeChannel.Preview))
            throw new ArgumentException("Cached API indexing only supports Release and Preview channels.", nameof(channel));
        if (commitSha is not { Length: 40 } || commitSha.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The upstream commit must be a 40-character lower-case SHA.", nameof(commitSha));

        var cache = new UpstreamSnapshotCache(_dataRoot);
        var cachedFiles = (await cache.ReadFilesAsync(codebase, commitSha, cancellationToken))
            .Where(file => file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (cachedFiles.Length == 0)
            throw new InvalidDataException("The cached upstream commit contains no C# source files.");

        var sourceIdentity = codebase + ":" + channel + ":" + commitSha;
        var indexId = IndexingWorkflow.CreateIndexId(
            sourceIdentity,
            "RoslynSourceIndexer",
            "1",
            force ? "cached-source:forced:" + Guid.NewGuid().ToString("N") : "cached-source",
            IndexSchemaVersion);
        var snapshotId = "api:" + codebase + ":" + channel + ":" + commitSha;
        if (await _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken) is null)
        {
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(snapshotId, codebase, channel, commitSha, DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken);
        }

        if (!force && await _repository.GetCompletedIndexAsync(indexId, cancellationToken) is not null)
        {
            var symbols = await _repository.GetCompletedSymbolsAsync(indexId, cancellationToken);
            var relationships = await _repository.GetCompletedRelationshipsAsync(indexId, cancellationToken);
            var sourceFiles = await _repository.GetCompletedSourceFilesAsync(indexId, cancellationToken);
            return new IndexingWorkflowResult(indexId, snapshotId, true, symbols.Count, sourceFiles.Count, relationships.Count, []);
        }

        var paths = OwnedIndexPaths.ForUpstreamIndex(_dataRoot, CodebaseDirectoryName(codebase), commitSha, indexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var run = new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O"));
        var runStarted = false;
        var databaseCompleted = false;
        try
        {
            await _repository.StartIndexRunAsync(run, cancellationToken);
            runStarted = true;
            var allSourceSymbols = new List<(UpstreamFile File, IReadOnlyList<NormalizedSymbol> Symbols, string Text)>();
            var sourceFiles = new List<IndexSourceFileRecord>(cachedFiles.Length);
            foreach (var file in cachedFiles)
            {
                var text = new UTF8Encoding(false, true).GetString(file.Content);
                var sourceFileId = HashId(snapshotId + "\n" + file.RelativePath);
                var sourceFile = new IndexSourceFileRecord(sourceFileId, snapshotId, file.RelativePath.Replace('\\', '/'), file.Sha256, file.Content.LongLength);
                var sourcePath = ResolveContainedPath(paths.StagingRoot, sourceFile.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllBytesAsync(sourcePath, file.Content, cancellationToken);
                var writtenHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath, cancellationToken))).ToLowerInvariant();
                if (!string.Equals(writtenHash, sourceFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Cached source hash validation failed for '{file.RelativePath}'.");
                sourceFiles.Add(sourceFile);
                allSourceSymbols.Add((file, _sourceIndexer.Index(text, codebase, channel, sourceFile.RelativePath), text));
            }

            var symbols = BuildCachedSymbols(allSourceSymbols, snapshotId, codebase, channel);
            var sourceLocations = BuildCachedSourceLocations(allSourceSymbols, symbols, sourceFiles);
            var fingerprints = BuildCachedSourceEvidence(allSourceSymbols, symbols);
            await _repository.CompleteIndexRunAsync(
                indexId,
                new IndexWriteSet(symbols, sourceFiles, sourceLocations, fingerprints, []),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
            databaseCompleted = true;
            if (Directory.Exists(paths.FinalRoot)) Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, cancellationToken);
            return new IndexingWorkflowResult(indexId, snapshotId, false, symbols.Count, sourceFiles.Count, 0, []);
        }
        catch (Exception exception)
        {
            if (runStarted && !databaseCompleted)
            {
                try { await _repository.FailIndexRunAsync(indexId, exception.Message, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None); } catch { }
            }
            if (Directory.Exists(paths.StagingRoot)) Directory.Delete(paths.StagingRoot, recursive: true);
            throw;
        }
    }

    private static IReadOnlyList<IndexSymbolRecord> BuildCachedSymbols(
        IReadOnlyList<(UpstreamFile File, IReadOnlyList<NormalizedSymbol> Symbols, string Text)> files,
        string snapshotId,
        CodebaseKind codebase,
        CodeChannel channel)
    {
        return files
            .SelectMany(item => item.Symbols)
            .GroupBy(symbol => SymbolIdentity.Create(symbol.Codebase, symbol.Channel, symbol.Kind, symbol.QualifiedName).CanonicalKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var symbol = group.First();
                var key = group.Key;
                return new IndexSymbolRecord(
                    HashId(snapshotId + "\n" + key),
                    snapshotId,
                    key,
                    symbol.Kind.ToString(),
                    symbol.QualifiedName,
                    symbol.Signature,
                    false);
            })
            .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<IndexSourceLocationRecord> BuildCachedSourceLocations(
        IReadOnlyList<(UpstreamFile File, IReadOnlyList<NormalizedSymbol> Symbols, string Text)> files,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyList<IndexSourceFileRecord> sourceFiles)
    {
        var ids = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        var fileIds = sourceFiles.ToDictionary(file => file.RelativePath, file => file.SourceFileId, StringComparer.Ordinal);
        return files
            .SelectMany(item => item.Symbols.Select(symbol => (item.File.RelativePath, Symbol: symbol)))
            .Select(item =>
            {
                var key = SymbolIdentity.Create(item.Symbol.Codebase, item.Symbol.Channel, item.Symbol.Kind, item.Symbol.QualifiedName).CanonicalKey;
                return ids.TryGetValue(key, out var symbolId) && fileIds.TryGetValue(item.RelativePath, out var fileId)
                    ? new IndexSourceLocationRecord(symbolId, fileId, item.Symbol.SourceLine ?? 1, item.Symbol.SourceColumn ?? 1, item.Symbol.SourceEndLine, item.Symbol.SourceEndColumn)
                    : null;
            })
            .Where(location => location is not null)
            .Cast<IndexSourceLocationRecord>()
            .GroupBy(location => location.SymbolId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(location => location.SourceFileId, StringComparer.Ordinal).ThenBy(location => location.StartLine).ThenBy(location => location.StartColumn).First())
            .OrderBy(location => location.SourceFileId, StringComparer.Ordinal)
            .ThenBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ToArray();
    }

    private static IReadOnlyList<IndexFingerprintRecord> BuildCachedSourceEvidence(
        IReadOnlyList<(UpstreamFile File, IReadOnlyList<NormalizedSymbol> Symbols, string Text)> files,
        IReadOnlyList<IndexSymbolRecord> symbols)
    {
        var ids = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        return files
            .SelectMany(item => item.Symbols.Select(symbol => (item, symbol)))
            .Select(item =>
            {
                var key = SymbolIdentity.Create(item.symbol.Codebase, item.symbol.Channel, item.symbol.Kind, item.symbol.QualifiedName).CanonicalKey;
                var evidence = item.symbol.SourceLine is int line
                    ? item.item.Text.Split('\n').ElementAtOrDefault(Math.Max(0, line - 1))?.TrimEnd('\r')
                    : null;
                return ids.TryGetValue(key, out var symbolId) && evidence is not null
                    ? new IndexFingerprintRecord(symbolId, "SourceLine", evidence)
                    : null;
            })
            .Where(fingerprint => fingerprint is not null)
            .Cast<IndexFingerprintRecord>()
            .GroupBy(fingerprint => fingerprint.SymbolId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(fingerprint => fingerprint.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("The cached source path is not safe.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("The cached source path escaped its owned index root.");
        return fullPath;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CodebaseDirectoryName(CodebaseKind codebase) => codebase switch
    {
        CodebaseKind.S1Api => "s1api",
        CodebaseKind.S1MApi => "s1mapi",
        _ => throw new ArgumentOutOfRangeException(nameof(codebase))
    };

    private static IReadOnlyList<IndexSymbolRecord> BuildSymbols(
        ManagedDecompilation decompilation,
        string snapshotId,
        CodebaseKind codebase)
    {
        var symbols = new List<IndexSymbolRecord>();
        foreach (var type in decompilation.Types)
        {
            var typeKey = SymbolIdentity.Create(codebase, CodeChannel.Installed, SymbolKind.Type, type.FullName).CanonicalKey;
            symbols.Add(new IndexSymbolRecord(HashId(snapshotId + "\n" + typeKey), snapshotId, typeKey, "Type", type.FullName, type.FullName, false));
            foreach (var member in type.Members)
            {
                var memberName = ManagedMemberIdentity.Render(type.FullName, member);
                var kind = member.Kind.ToString();
                var key = SymbolIdentity.Create(codebase, CodeChannel.Installed, Enum.Parse<SymbolKind>(kind), memberName).CanonicalKey;
                var bodyRecoveryStatus = member.Kind is ManagedMemberKind.Constructor or ManagedMemberKind.Method
                    ? member.BodyRecoveryStatus
                    : null;
                symbols.Add(new IndexSymbolRecord(HashId(snapshotId + "\n" + key), snapshotId, key, kind, memberName, member.Signature, false, bodyRecoveryStatus));
            }
        }
        return symbols
            .GroupBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<IndexSourceLocationRecord> BuildSourceLocations(
        IReadOnlyList<NormalizedSymbol> sourceSymbols,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IndexSourceFileRecord sourceFile)
    {
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        return sourceSymbols
            .Where(symbol => symbol.SourceLine is not null)
            .Select(symbol =>
            {
                var key = SymbolIdentity.Create(symbol.Codebase, symbol.Channel, symbol.Kind, symbol.QualifiedName).CanonicalKey;
                return symbolIds.TryGetValue(key, out var symbolId)
                    ? new IndexSourceLocationRecord(symbolId, sourceFile.SourceFileId, symbol.SourceLine!.Value, symbol.SourceColumn ?? 1, symbol.SourceEndLine, symbol.SourceEndColumn)
                    : null;
            })
            .Where(location => location is not null)
            .Cast<IndexSourceLocationRecord>()
            .GroupBy(location => location.SymbolId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(location => location.StartLine).ThenBy(location => location.StartColumn).First())
            .OrderBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ThenBy(location => location.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildMethodEvidence(
        ManagedDecompilation decompilation,
        IReadOnlyList<IndexSymbolRecord> symbols,
        CodebaseKind codebase)
    {
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        return decompilation.Types
            .SelectMany(type => type.Members.Select(member => (type, member)))
            .Where(item => item.member.Kind is ManagedMemberKind.Constructor or ManagedMemberKind.Method && item.member.HasBody)
            .Select(item =>
            {
                var key = SymbolIdentity.Create(codebase, CodeChannel.Installed, ToSymbolKind(item.member.Kind), ManagedMemberIdentity.Render(item.type.FullName, item.member)).CanonicalKey;
                return symbolIds.TryGetValue(key, out var symbolId)
                    ? (symbolId, Evidence: (IReadOnlyList<string>)item.member.References.Select(reference => reference.Kind + ":" + reference.Target).ToArray())
                    : (null, Evidence: (IReadOnlyList<string>)[]);
            })
            .Where(item => item.symbolId is not null && item.Evidence.Count > 0)
            .ToDictionary(item => item.symbolId!, item => item.Evidence, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildSourceEvidence(
        IReadOnlyList<NormalizedSymbol> sourceSymbols,
        IReadOnlyList<IndexSymbolRecord> symbols,
        string sourceText)
    {
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        var lines = sourceText.Split('\n');
        return sourceSymbols
            .Where(symbol => symbol.SourceLine is not null)
            .Select(symbol =>
            {
                var key = SymbolIdentity.Create(symbol.Codebase, symbol.Channel, symbol.Kind, symbol.QualifiedName).CanonicalKey;
                var line = lines[Math.Clamp(symbol.SourceLine!.Value - 1, 0, Math.Max(0, lines.Length - 1))].TrimEnd('\r');
                return symbolIds.TryGetValue(key, out var symbolId)
                    ? (symbolId, Evidence: (IReadOnlyList<string>)[line])
                    : (null, Evidence: (IReadOnlyList<string>)[]);
            })
            .Where(item => item.symbolId is not null && item.Evidence.Count > 0)
            .GroupBy(item => item.symbolId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.SelectMany(item => item.Evidence).ToArray(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<IndexRelationshipRecord> BuildRelationships(
        ManagedDecompilation decompilation,
        IReadOnlyList<IndexSymbolRecord> symbols,
        string snapshotId,
        CodebaseKind codebase)
    {
        var facts = new RelationshipExtractor().Extract(decompilation, codebase, CodeChannel.Installed);
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        return facts
            .Where(fact => symbolIds.ContainsKey(fact.SourceKey))
            .Select(fact => new IndexRelationshipRecord(
                HashId(snapshotId + "\n" + fact.SourceKey + "\n" + fact.Kind + "\n" + fact.TargetText),
                snapshotId,
                symbolIds[fact.SourceKey],
                fact.TargetKey is not null && symbolIds.TryGetValue(fact.TargetKey, out var targetSymbolId) ? targetSymbolId : null,
                fact.TargetText,
                fact.Kind.ToString(),
                fact.Evidence.ToString()))
            .GroupBy(relationship => relationship.RelationshipId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(relationship => relationship.RelationshipId, StringComparer.Ordinal)
            .ToArray();
    }

    private static SymbolKind ToSymbolKind(ManagedMemberKind kind) => kind switch
    {
        ManagedMemberKind.Constructor => SymbolKind.Constructor,
        ManagedMemberKind.Method => SymbolKind.Method,
        ManagedMemberKind.Field => SymbolKind.Field,
        ManagedMemberKind.Property => SymbolKind.Property,
        ManagedMemberKind.Event => SymbolKind.Event,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
