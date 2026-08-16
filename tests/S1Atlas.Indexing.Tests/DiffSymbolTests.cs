using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class DiffSymbolTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-symbol-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public DiffSymbolTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteAtlasRepository(Path.Combine(_directory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task DiffSymbol_IdenticalSymbol_ClassifiesUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [],
            relationshipsB: [],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.Unchanged, diff!.Classification);
        Assert.Equal("N.T.M()", diff.CanonicalKey);
        Assert.Equal("N.T.M()", diff.SignatureBefore);
        Assert.Equal("N.T.M()", diff.SignatureAfter);
    }

    [Fact]
    public async Task DiffSymbol_OnlyInB_ClassifiesAdded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [],
            symbolsB: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()")],
            fingerprintsA: [],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [],
            relationshipsB: [],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.Added, diff!.Classification);
        Assert.Null(diff.SignatureBefore);
        Assert.Equal("N.T.M()", diff.SignatureAfter);
    }

    [Fact]
    public async Task DiffSymbol_OnlyInA_ClassifiesRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()")],
            symbolsB: [],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [],
            relationshipsA: [],
            relationshipsB: [],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.Removed, diff!.Classification);
        Assert.Equal("N.T.M()", diff.SignatureBefore);
        Assert.Null(diff.SignatureAfter);
    }

    [Fact]
    public async Task DiffSymbol_ChangedBody_ClassifiesMethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol("N.T.M()", "Method", "N.T.M", "N.T.M()", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "old")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-0", "method-body", "new")],
            relationshipsA: [],
            relationshipsB: [],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.MethodBodyChanged, diff!.Classification);
        Assert.Equal("N.T.M()", diff.SignatureBefore);
        Assert.Equal("N.T.M()", diff.SignatureAfter);
    }

    [Fact]
    public async Task DiffSymbol_ChangedEdges_ClassifiesRelationshipsChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("N.T.M()", "Type", "N.T.M", "N.T.M()")],
            symbolsB: [MakeSymbol("N.T.M()", "Type", "N.T.M", "N.T.M()")],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [MakeRelationship("rel-a-0", "sym-a-0", null, "System.Object", "Inherits")],
            relationshipsB: [MakeRelationship("rel-b-0", "sym-b-0", null, "System.Exception", "Inherits")],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.RelationshipsChanged, diff!.Classification);
        Assert.Equal("N.T.M()", diff.SignatureBefore);
        Assert.Equal("N.T.M()", diff.SignatureAfter);
    }

    [Fact]
    public async Task DiffSymbol_AbsentInBoth_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [],
            symbolsB: [],
            fingerprintsA: [],
            fingerprintsB: [],
            relationshipsA: [],
            relationshipsB: [],
            ct);

        var service = new BuildDiffService(_repository);
        var diff = await service.DiffSymbolAsync(idA, idB, "ScheduleI", "Installed", "N.T.M()", ct);

        Assert.Null(diff);
    }

    private IndexSymbolRecord MakeSymbol(string canonicalKey, string kind, string qualifiedName, string signature, BodyRecoveryStatus? bodyStatus = null) =>
        new("placeholder", "placeholder", canonicalKey, kind, qualifiedName, signature, false, bodyStatus);

    private IndexFingerprintRecord MakeFingerprint(string symbolId, string kind, string hash) =>
        new(symbolId, kind, hash);

    private IndexRelationshipRecord MakeRelationship(string relId, string sourceSymbolId, string? targetSymbolId, string targetText, string kind) =>
        new(relId, "placeholder", sourceSymbolId, targetSymbolId, targetText, kind, "Metadata");

    private async Task<(string idA, string idB)> SeedTwoIndexesAsync(
        IReadOnlyList<IndexSymbolRecord> symbolsA,
        IReadOnlyList<IndexSymbolRecord> symbolsB,
        IReadOnlyList<IndexFingerprintRecord> fingerprintsA,
        IReadOnlyList<IndexFingerprintRecord> fingerprintsB,
        IReadOnlyList<IndexRelationshipRecord> relationshipsA,
        IReadOnlyList<IndexRelationshipRecord> relationshipsB,
        CancellationToken ct)
    {
        var idA = "idx-a-" + Guid.NewGuid().ToString("N")[..8];
        var idB = "idx-b-" + Guid.NewGuid().ToString("N")[..8];
        var snapA = "snap-a-" + Guid.NewGuid().ToString("N")[..8];
        var snapB = "snap-b-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow.ToString("O");

        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapA, CodebaseKind.ScheduleI, CodeChannel.Installed, "ext-a", now), ct);
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapB, CodebaseKind.ScheduleI, CodeChannel.Installed, "ext-b", now), ct);
        await _repository.StartIndexRunAsync(new IndexRunRecord(idA, snapA, IndexRunStatus.Running, now), ct);
        await _repository.StartIndexRunAsync(new IndexRunRecord(idB, snapB, IndexRunStatus.Running, now), ct);

        var realSymbolsA = symbolsA.Select((s, i) => s with { SymbolId = $"sym-a-{i}", SnapshotId = snapA }).ToArray();
        var realSymbolsB = symbolsB.Select((s, i) => s with { SymbolId = $"sym-b-{i}", SnapshotId = snapB }).ToArray();
        var realFpA = fingerprintsA.ToArray();
        var realFpB = fingerprintsB.ToArray();
        var realRelA = relationshipsA.Select(r => r with { SnapshotId = snapA }).ToArray();
        var realRelB = relationshipsB.Select(r => r with { SnapshotId = snapB }).ToArray();

        await _repository.CompleteIndexRunAsync(idA, new IndexWriteSet(realSymbolsA, [], [], realFpA, realRelA), now, ct);
        await _repository.CompleteIndexRunAsync(idB, new IndexWriteSet(realSymbolsB, [], [], realFpB, realRelB), now, ct);
        return (idA, idB);
    }
}
