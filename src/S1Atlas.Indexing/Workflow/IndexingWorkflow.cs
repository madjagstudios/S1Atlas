using System.Security.Cryptography;
using System.Text;
using ICSharpCode.Decompiler.CSharp;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Source;
using S1Atlas.Indexing.Relationships;

namespace S1Atlas.Indexing.Workflow;

public sealed record IndexingWorkflowResult(
    string IndexId,
    string SnapshotId,
    bool Reused,
    int SymbolCount,
    int SourceFileCount,
    int RelationshipCount,
    IReadOnlyList<string> Warnings,
    int CallableSurfaceCount = 0,
    int? ReferenceModCount = null,
    int? ReferenceDocumentCount = null,
    int? ReferenceSymbolCount = null,
    long? InputHashMilliseconds = null);

public sealed class IndexingWorkflow
{
    public const int IndexSchemaVersion = 9;
    internal const string DecompilerPackage = "ICSharpCode.Decompiler";
    internal static string DecompilerVersion => typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The ILSpy decompiler assembly has no version.");

    private readonly string _dataRoot;
    private readonly IIndexRepository _repository;
    private readonly Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> _authorityResolver;
    private readonly ScheduleOneIndexSource _source;
    private readonly GeneratedSourceWriter _sourceWriter = new();
    private readonly RoslynSourceIndexer _sourceIndexer = new();
    private readonly SymbolFingerprintService _fingerprints = new();
    private readonly RelationshipExtractor _relationships = new();
    private readonly IAtlasRepository? _atlasRepository;

    public IndexingWorkflow(
        string dataRoot,
        IIndexRepository repository,
        Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> authorityResolver,
        ScheduleOneIndexSource source,
        IAtlasRepository? atlasRepository = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _atlasRepository = atlasRepository;
    }

    public static string CreateIndexId(
        string extractionId,
        string package,
        string version,
        string settings,
        int schemaVersion,
        string? interopInputSha256 = null)
    {
        var input = string.Join(
            "\n",
            extractionId,
            package,
            version,
            settings,
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            interopInputSha256 ?? "none");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public async Task<IndexingWorkflowResult> RunScheduleOneAsync(
        string buildId,
        bool force,
        CancellationToken cancellationToken,
        string? interopPath = null)
    {
        var authority = await _authorityResolver(buildId, cancellationToken)
            ?? throw new InvalidOperationException("No preferred integrity-verified extraction is available.");
        var selectedInteropPath = await ResolveInteropPathAsync(buildId, interopPath, cancellationToken);
        var interopInputSha256 = selectedInteropPath is null
            ? null
            : await HashFileAsync(selectedInteropPath, cancellationToken);
        var indexId = CreateIndexId(
            authority.Extraction.ExtractionId,
            DecompilerPackage,
            DecompilerVersion,
            force ? "default:forced:" + Guid.NewGuid().ToString("N") : "default",
            IndexSchemaVersion,
            interopInputSha256);
        var snapshotId = "schedule-i:" + authority.Extraction.ExtractionId + ":" + indexId;

        var existingSnapshot = await _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken);
        if (existingSnapshot is null)
        {
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, authority.Extraction.ExtractionId, DateTimeOffset.UtcNow.ToString("O")),
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
                var callableSurface = await _repository.GetCompletedCallableSurfaceAsync(indexId, cancellationToken);
                return new IndexingWorkflowResult(indexId, snapshotId, true, symbols.Count, sourceFiles.Count, relationships.Count, [], callableSurface.Count);
            }
        }

        var paths = OwnedIndexPaths.ForScheduleOne(_dataRoot, buildId, indexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var run = new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O"));
        var runStarted = false;
        var databaseCompleted = false;
        try
        {
            await _repository.StartIndexRunAsync(run, cancellationToken);
            runStarted = true;
            var decompilation = await _source.ReadAsync(authority, cancellationToken);
            var finalAuthority = await _authorityResolver(buildId, cancellationToken);
            if (finalAuthority is null || finalAuthority.Extraction.ExtractionId != authority.Extraction.ExtractionId)
                throw new InvalidOperationException("The preferred extraction changed during indexing.");
            var sourceFile = await _sourceWriter.WriteAsync(paths.StagingRoot, "Assembly-CSharp.cs", decompilation.SourceText, snapshotId, cancellationToken);
            ManagedDecompilation? interopDecompilation = null;
            IndexSourceFileRecord? interopSourceFile = null;
            if (selectedInteropPath is not null)
            {
                interopDecompilation = await _source.ReadInteropAsync(selectedInteropPath, cancellationToken);
                var finalInteropHash = await HashFileAsync(selectedInteropPath, cancellationToken);
                if (!string.Equals(interopInputSha256, finalInteropHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The interop assembly changed during indexing.");
                interopSourceFile = await _sourceWriter.WriteAsync(
                    paths.StagingRoot,
                    "interop/Assembly-CSharp.cs",
                    interopDecompilation.SourceText,
                    snapshotId,
                    cancellationToken);
            }
            var symbols = BuildSymbols(decompilation, snapshotId);
            var callableSurface = BuildCallableSurface(
                decompilation,
                interopDecompilation,
                symbols,
                indexId,
                snapshotId,
                selectedInteropPath,
                interopInputSha256);
            var sourceSymbols = _sourceIndexer.Index(decompilation.SourceText, CodebaseKind.ScheduleI, CodeChannel.Installed, sourceFile.RelativePath);
            var sourceLocations = BuildSourceLocations(sourceSymbols, symbols, sourceFile);
            var fingerprints = _fingerprints.Create(
                symbols,
                BuildMethodEvidence(decompilation, symbols),
                BuildSourceEvidence(sourceSymbols, symbols, decompilation.SourceText));
            var relationships = BuildRelationships(decompilation, symbols, snapshotId);

            var writtenPath = Path.Combine(paths.StagingRoot, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var writtenHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(writtenPath, cancellationToken))).ToLowerInvariant();
            if (!string.Equals(writtenHash, sourceFile.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Generated source hash validation failed.");

            var sourceFiles = interopSourceFile is null ? new[] { sourceFile } : new[] { sourceFile, interopSourceFile };
            await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, sourceFiles, sourceLocations, fingerprints, relationships, callableSurface), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            databaseCompleted = true;
            if (Directory.Exists(paths.FinalRoot)) Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, cancellationToken);
            var warnings = selectedInteropPath is null
                ? new[] { "InteropSurfaceUnavailable: no usable Il2CppInterop Assembly-CSharp.dll was found; wrapper-dependent members are unavailable." }
                : Array.Empty<string>();
            return new IndexingWorkflowResult(indexId, snapshotId, false, symbols.Count, sourceFiles.Length, relationships.Count, warnings, callableSurface.Count);
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

    private async Task<string?> ResolveInteropPathAsync(
        string buildId,
        string? overridePath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return ResolveInteropPath(overridePath);

        if (_atlasRepository is null)
            return null;
        var current = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        if (current is null || !string.Equals(current.Build.BuildId, buildId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(current.Installation.InstallationRoot))
            return null;
        return ResolveInteropPath(Path.Combine(
            current.Installation.InstallationRoot,
            "MelonLoader",
            "Il2CppAssemblies",
            "Assembly-CSharp.dll"));
    }

    private static string? ResolveInteropPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            fullPath = Path.Combine(fullPath, "Il2CppAssemblies", "Assembly-CSharp.dll");
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static IReadOnlyList<IndexCallableSurfaceRecord> BuildCallableSurface(
        ManagedDecompilation game,
        ManagedDecompilation? interop,
        IReadOnlyList<IndexSymbolRecord> symbols,
        string indexId,
        string snapshotId,
        string? interopPath,
        string? interopInputSha256)
    {
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol, StringComparer.Ordinal);
        var matches = new InteropCallableSurfaceMatcher().Match(game, interop);
        var assemblyName = interopPath is null ? "Assembly-CSharp.dll" : Path.GetFileName(interopPath);
        return matches
            .Select(match =>
            {
                var memberName = ManagedMemberIdentity.Render(match.GameTypeName, match.GameMember);
                var key = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, ToSymbolKind(match.GameMember.Kind), memberName).CanonicalKey;
                return symbolIds.TryGetValue(key, out var symbol)
                    ? new IndexCallableSurfaceRecord(
                        HashId(indexId + "\n" + symbol.SymbolId),
                        indexId,
                        snapshotId,
                        symbol.SymbolId,
                        symbol.CanonicalKey,
                        assemblyName,
                        interopInputSha256,
                        match.InteropSignature,
                        match.Kind,
                        match.RequiresReflection,
                        match.Status,
                        InteropInputTrust.LocalOnly,
                        match.Evidence)
                    : null;
            })
            .Where(record => record is not null)
            .Cast<IndexCallableSurfaceRecord>()
            .OrderBy(record => record.GameCanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<IndexSymbolRecord> BuildSymbols(ManagedDecompilation decompilation, string snapshotId)
    {
        var symbols = new List<IndexSymbolRecord>();
        foreach (var type in decompilation.Types)
        {
            var typeKey = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Type, type.FullName).CanonicalKey;
            symbols.Add(new IndexSymbolRecord(HashId(snapshotId + "\n" + typeKey), snapshotId, typeKey, "Type", type.FullName, type.FullName, false));
            foreach (var member in type.Members)
            {
                var memberName = ManagedMemberIdentity.Render(type.FullName, member);
                var kind = member.Kind.ToString();
                var key = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, Enum.Parse<SymbolKind>(kind), memberName).CanonicalKey;
                var bodyRecoveryStatus = member.Kind is ManagedMemberKind.Constructor or ManagedMemberKind.Method
                    ? member.BodyRecoveryStatus
                    : null;
                symbols.Add(new IndexSymbolRecord(
                    HashId(snapshotId + "\n" + key),
                    snapshotId,
                    key,
                    kind,
                    memberName,
                    member.Signature,
                    false,
                    bodyRecoveryStatus,
                    member.IsPublic));
            }
        }
        return symbols
            .GroupBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
                    ? new IndexSourceLocationRecord(
                        symbolId,
                        sourceFile.SourceFileId,
                        symbol.SourceLine!.Value,
                        symbol.SourceColumn ?? 1,
                        symbol.SourceEndLine,
                        symbol.SourceEndColumn)
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
        IReadOnlyList<IndexSymbolRecord> symbols)
    {
        var symbolIds = symbols.ToDictionary(symbol => symbol.CanonicalKey, symbol => symbol.SymbolId, StringComparer.Ordinal);
        return decompilation.Types
            .SelectMany(type => type.Members.Select(member => (type, member)))
            .Where(item => item.member.Kind is ManagedMemberKind.Constructor or ManagedMemberKind.Method && item.member.HasBody)
            .Select(item =>
            {
                var key = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, ToSymbolKind(item.member.Kind), ManagedMemberIdentity.Render(item.type.FullName, item.member)).CanonicalKey;
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

    private IReadOnlyList<IndexRelationshipRecord> BuildRelationships(
        ManagedDecompilation decompilation,
        IReadOnlyList<IndexSymbolRecord> symbols,
        string snapshotId)
    {
        var facts = _relationships.Extract(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);
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
