using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneCodeSymbolResolverTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-symbols-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SceneCodeSymbolResolverTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Exact_schedule_i_installed_type_match_resolves()
    {
        var symbol = await SeedAsync("index-a", "extraction-a", CodebaseKind.ScheduleI, CodeChannel.Installed, 1);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "build-a",
            "extraction-a",
            "index-a",
            Script(),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.Resolved, result.Status);
        Assert.Equal(symbol.SymbolId, result.SymbolId);
        Assert.Equal("index-a", result.CodeIndexId);
        Assert.Equal("ScheduleOne.PlayerController", result.NormalizedQualifiedName);
    }

    [Fact]
    public async Task Absent_exact_symbol_is_not_indexed_without_fuzzy_matching()
    {
        await SeedAsync("index-a", "extraction-a", CodebaseKind.ScheduleI, CodeChannel.Installed, 0, includeFuzzy: true);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(), TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.NotIndexed, result.Status);
        Assert.Null(result.SymbolId);
        Assert.Equal("Assembly-CSharp.dll", result.RawAssemblyName);
        Assert.Equal("ScheduleOne", result.RawNamespace);
        Assert.Equal("PlayerController", result.RawClassName);
    }

    [Fact]
    public async Task More_than_one_exact_candidate_is_ambiguous()
    {
        var symbol = await SeedAsync("index-a", "extraction-a", CodebaseKind.ScheduleI, CodeChannel.Installed, 1);
        var resolver = new SceneCodeSymbolResolver(
            new DuplicateExactIndexRepository(_repository, symbol),
            (_, _) => Task.FromResult<SceneCodeBuildAuthority?>(new("extraction-a", "build-a")));

        var result = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(), TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.SymbolId);
    }

    [Fact]
    public async Task Different_build_authority_is_rejected_as_a_build_mismatch()
    {
        await SeedAsync("index-a", "extraction-a", CodebaseKind.ScheduleI, CodeChannel.Installed, 1);
        var resolver = CreateResolver("build-other");

        var result = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(), TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.SymbolId);
    }

    [Theory]
    [InlineData(CodebaseKind.S1Api, CodeChannel.Installed)]
    [InlineData(CodebaseKind.S1MApi, CodeChannel.Release)]
    public async Task Non_schedule_i_installed_indexes_never_resolve(CodebaseKind codebase, CodeChannel channel)
    {
        await SeedAsync("index-a", "extraction-a", codebase, channel, 1);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(), TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.SymbolId);
    }

    [Fact]
    public async Task Conflicting_assembly_or_missing_type_text_remains_unresolved_text()
    {
        await SeedAsync("index-a", "extraction-a", CodebaseKind.ScheduleI, CodeChannel.Installed, 1);
        var resolver = CreateResolver();

        var conflict = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(assembly: "Other.dll"), TestContext.Current.CancellationToken);
        var missing = await resolver.ResolveAsync("build-a", "extraction-a", "index-a", Script(className: ""), TestContext.Current.CancellationToken);

        Assert.Equal(SceneResolutionStatus.UnresolvedText, conflict.Status);
        Assert.Equal(SceneResolutionStatus.UnresolvedText, missing.Status);
        Assert.Null(conflict.SymbolId);
        Assert.Null(missing.SymbolId);
    }

    private async Task<IndexSymbolRecord> SeedAsync(
        string indexId,
        string sourceIdentity,
        CodebaseKind codebase,
        CodeChannel channel,
        int exactCount,
        bool includeFuzzy = false)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        var snapshotId = "snapshot-" + Guid.NewGuid().ToString("N");
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, codebase, channel, sourceIdentity, "2026-08-15T00:00:00Z"),
            TestContext.Current.CancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, "2026-08-15T00:00:00Z"),
            TestContext.Current.CancellationToken);

        var canonicalKey = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Type, "ScheduleOne.PlayerController").CanonicalKey;
        var symbols = Enumerable.Range(0, exactCount)
            .Select(index => new IndexSymbolRecord(Hash(snapshotId + index), snapshotId, canonicalKey, "Type", "ScheduleOne.PlayerController", "ScheduleOne.PlayerController", false))
            .ToList();
        if (includeFuzzy)
        {
            symbols.Add(new IndexSymbolRecord(Hash(snapshotId + "fuzzy"), snapshotId, canonicalKey + "Proxy", "Type", "ScheduleOne.PlayerControllerProxy", "ScheduleOne.PlayerControllerProxy", false));
        }

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-15T00:01:00Z",
            TestContext.Current.CancellationToken);
        return symbols.FirstOrDefault() ?? new IndexSymbolRecord("unused", snapshotId, canonicalKey, "Type", "ScheduleOne.PlayerController", "ScheduleOne.PlayerController", false);
    }

    private SceneCodeSymbolResolver CreateResolver(string buildId = "build-a") =>
        new(
            _repository,
            (extractionId, _) => Task.FromResult<SceneCodeBuildAuthority?>(
                new SceneCodeBuildAuthority(extractionId, buildId)));

    private static ParsedMonoScriptData Script(
        string assembly = "Assembly-CSharp.dll",
        string @namespace = "ScheduleOne",
        string className = "PlayerController") =>
        new(assembly, @namespace, className);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class DuplicateExactIndexRepository : IIndexRepository
    {
        private readonly IIndexRepository _inner;
        private readonly IndexSymbolRecord _symbol;

        public DuplicateExactIndexRepository(IIndexRepository inner, IndexSymbolRecord symbol)
        {
            _inner = inner;
            _symbol = symbol;
        }

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexSymbolRecord>>([
                _symbol,
                _symbol with { SymbolId = Hash(_symbol.SymbolId + "duplicate") }
            ]);

        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => _inner.GetCodeSnapshotAsync(snapshotId, cancellationToken);
        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedIndexAsync(indexId, cancellationToken);
        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) => _inner.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) => _inner.StartIndexRunAsync(run, cancellationToken);
        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => _inner.CompleteIndexRunAsync(indexId, writeSet, completedAtUtc, cancellationToken);
        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => _inner.FailIndexRunAsync(indexId, failureMessage, completedAtUtc, cancellationToken);
        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) => _inner.GetLatestCompletedIndexAsync(codebase, channel, environmentSnapshotId, cancellationToken);
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedSymbolsAsync(indexId, cancellationToken);
        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => _inner.GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken);
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) => _inner.GetCompletedSymbolsByIdsAsync(indexId, symbolIds, cancellationToken);
        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) => _inner.CountCompletedSymbolMatchesAsync(indexId, query, cancellationToken, kind);
        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) => _inner.SearchCompletedSymbolsAsync(indexId, query, limit, cancellationToken, kind);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedRelationshipsAsync(indexId, cancellationToken);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => _inner.GetCompletedRelationshipsBySourceSymbolIdAsync(indexId, symbolId, cancellationToken);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, cancellationToken);
        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) => _inner.CountCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, cancellationToken);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) => _inner.GetCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, limit, cancellationToken);
        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) => _inner.CountCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, cancellationToken);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) => _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, limit, cancellationToken);
        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedSourceFilesAsync(indexId, cancellationToken);
        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedSourceLocationsAsync(indexId, cancellationToken);
        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) => _inner.GetCompletedFingerprintsAsync(indexId, cancellationToken);
        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) => _inner.GetLatestCompletedIndexBySourceIdentityAsync(codebase, channel, sourceIdentity, cancellationToken);
        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) => _inner.GetLatestCompletedIndexForBuildAsync(codebase, channel, buildId, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
