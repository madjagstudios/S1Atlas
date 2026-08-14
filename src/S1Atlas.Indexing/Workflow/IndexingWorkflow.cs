using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Source;

namespace S1Atlas.Indexing.Workflow;

public sealed record IndexingWorkflowResult(
    string IndexId,
    string SnapshotId,
    bool Reused,
    int SymbolCount,
    int SourceFileCount,
    int RelationshipCount,
    IReadOnlyList<string> Warnings);

public sealed class IndexingWorkflow
{
    public const int IndexSchemaVersion = 6;
    private const string DecompilerPackage = "ICSharpCode.Decompiler";
    private const string DecompilerVersion = "10.1.1.8388";

    private readonly string _dataRoot;
    private readonly IIndexRepository _repository;
    private readonly Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> _authorityResolver;
    private readonly ScheduleOneIndexSource _source;
    private readonly GeneratedSourceWriter _sourceWriter = new();
    private readonly SymbolFingerprintService _fingerprints = new();

    public IndexingWorkflow(
        string dataRoot,
        IIndexRepository repository,
        Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> authorityResolver,
        ScheduleOneIndexSource source)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public static string CreateIndexId(string extractionId, string package, string version, string settings, int schemaVersion)
    {
        var input = string.Join("\n", extractionId, package, version, settings, schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public async Task<IndexingWorkflowResult> RunScheduleOneAsync(
        string buildId,
        bool force,
        CancellationToken cancellationToken)
    {
        var authority = await _authorityResolver(buildId, cancellationToken)
            ?? throw new InvalidOperationException("No preferred integrity-verified extraction is available.");
        var indexId = CreateIndexId(
            authority.Extraction.ExtractionId,
            DecompilerPackage,
            DecompilerVersion,
            force ? "default:forced:" + Guid.NewGuid().ToString("N") : "default",
            IndexSchemaVersion);
        var snapshotId = "schedule-i:" + authority.Extraction.ExtractionId;

        var existingSnapshot = await _repository.GetCodeSnapshotAsync(snapshotId, cancellationToken);
        if (existingSnapshot is null)
        {
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, authority.Extraction.ExtractionId, DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken);
        }

        if (!force)
        {
            var existing = await _repository.GetLatestCompletedIndexAsync(CodebaseKind.ScheduleI, CodeChannel.Installed, null, cancellationToken);
            if (existing?.IndexId == indexId)
            {
                var symbols = await _repository.GetCompletedSymbolsAsync(indexId, cancellationToken);
                return new IndexingWorkflowResult(indexId, snapshotId, true, symbols.Count, 0, 0, []);
            }
        }

        var paths = OwnedIndexPaths.ForScheduleOne(_dataRoot, buildId, indexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var run = new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O"));
        await _repository.StartIndexRunAsync(run, cancellationToken);
        try
        {
            var decompilation = await _source.ReadAsync(authority, cancellationToken);
            var finalAuthority = await _authorityResolver(buildId, cancellationToken);
            if (finalAuthority is null || finalAuthority.Extraction.ExtractionId != authority.Extraction.ExtractionId)
                throw new InvalidOperationException("The preferred extraction changed during indexing.");
            var sourceFile = await _sourceWriter.WriteAsync(paths.StagingRoot, "Assembly-CSharp.cs", decompilation.SourceText, snapshotId, cancellationToken);
            var symbols = BuildSymbols(decompilation, snapshotId);
            var fingerprints = _fingerprints.Create(symbols);

            var writtenPath = Path.Combine(paths.StagingRoot, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var writtenHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(writtenPath, cancellationToken))).ToLowerInvariant();
            if (!string.Equals(writtenHash, sourceFile.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Generated source hash validation failed.");

            if (Directory.Exists(paths.FinalRoot)) Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, [sourceFile], [], fingerprints, []), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, cancellationToken);
            return new IndexingWorkflowResult(indexId, snapshotId, false, symbols.Count, 1, 0, []);
        }
        catch (Exception exception)
        {
            try { await _repository.FailIndexRunAsync(indexId, exception.Message, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None); } catch { }
            if (Directory.Exists(paths.StagingRoot)) Directory.Delete(paths.StagingRoot, recursive: true);
            throw;
        }
    }

    private static IReadOnlyList<IndexSymbolRecord> BuildSymbols(ManagedDecompilation decompilation, string snapshotId)
    {
        var symbols = new List<IndexSymbolRecord>();
        foreach (var type in decompilation.Types)
        {
            var typeKey = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Type, type.FullName).CanonicalKey;
            symbols.Add(new IndexSymbolRecord(HashId(typeKey), snapshotId, typeKey, "Type", type.FullName, type.FullName, false));
            foreach (var member in type.Members)
            {
                var memberName = type.FullName + "::" + member.Signature;
                var kind = member.Kind.ToString();
                var key = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, Enum.Parse<SymbolKind>(kind), memberName).CanonicalKey;
                symbols.Add(new IndexSymbolRecord(HashId(key), snapshotId, key, kind, memberName, member.Signature, false));
            }
        }
        return symbols
            .GroupBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
