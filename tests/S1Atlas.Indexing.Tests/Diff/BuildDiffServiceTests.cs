using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Diff;

public sealed class BuildDiffServiceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-svc-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;
    private readonly BuildDiffService _service;

    public BuildDiffServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteAtlasRepository(Path.Combine(_directory, "atlas.db"));
        _service = new BuildDiffService(_repository);
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Symbol_in_B_only_is_Added()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [],
            symbolsB: [MakeSymbol("ScheduleI:Installed:Method:Foo::Bar():System.Void", "Method", "Foo.Bar", "Foo::Bar():System.Void")],
            fingerprintsA: [], fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "abc")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.Added, result.Changes[0].Classification);
        Assert.Equal("Foo.Bar", result.Changes[0].QualifiedName);
        Assert.Null(result.Changes[0].SignatureBefore);
        Assert.Equal("Foo::Bar():System.Void", result.Changes[0].SignatureAfter);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Added]);
    }

    [Fact]
    public async Task Symbol_in_A_only_is_Removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("ScheduleI:Installed:Method:Foo::Bar():System.Void", "Method", "Foo.Bar", "Foo::Bar():System.Void")],
            symbolsB: [],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "abc")],
            fingerprintsB: [],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.Removed, result.Changes[0].Classification);
        Assert.Equal("Foo::Bar():System.Void", result.Changes[0].SignatureBefore);
        Assert.Null(result.Changes[0].SignatureAfter);
    }

    [Fact]
    public async Task Matching_symbol_with_different_method_body_fingerprint_is_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Do():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Do", "Foo::Do():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Do", "Foo::Do():System.Void", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "old-hash")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-0", "method-body", "new-hash")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.MethodBodyChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Matching_symbol_with_different_relationships_is_RelationshipsChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Type:MyNs.MyType";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Type", "MyNs.MyType", "MyNs.MyType")],
            symbolsB: [MakeSymbol(key, "Type", "MyNs.MyType", "MyNs.MyType")],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [MakeRelationship("rel-a-0", "sym-a-0", null, "System.Object", "Inherits")],
            relationshipsB: [MakeRelationship("rel-b-0", "sym-b-0", null, "System.Exception", "Inherits")],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.RelationshipsChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Matching_symbol_with_identical_evidence_is_Unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Type:MyNs.Stable";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Type", "MyNs.Stable", "MyNs.Stable")],
            symbolsB: [MakeSymbol(key, "Type", "MyNs.Stable", "MyNs.Stable")],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [MakeRelationship("rel-a-0", "sym-a-0", null, "System.Object", "Inherits")],
            relationshipsB: [MakeRelationship("rel-b-0", "sym-b-0", null, "System.Object", "Inherits")],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Empty(result.Changes);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
    }

    [Fact]
    public async Task Asymmetric_body_fingerprint_with_Recovered_status_is_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Act():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Act", "Foo::Act():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Act", "Foo::Act():System.Void", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "has-refs")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.MethodBodyChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Asymmetric_body_fingerprint_with_unavailable_status_skips_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Stub():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Stub", "Foo::Stub():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Stub", "Foo::Stub():System.Void", BodyRecoveryStatus.StubOrUnavailable)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "has-refs")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Empty(result.Changes);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
    }

    [Fact]
    public async Task Kind_filter_excludes_non_matching_symbols_from_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var methodKey = "ScheduleI:Installed:Method:Foo::Do():System.Void";
        var typeKey = "ScheduleI:Installed:Type:MyNs.MyType";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [
                MakeSymbol(methodKey, "Method", "Foo.Do", "Foo::Do():System.Void"),
                MakeSymbol(typeKey, "Type", "MyNs.MyType", "MyNs.MyType")
            ],
            symbolsB: [
                MakeSymbol(methodKey, "Method", "Foo.Do", "Foo::Do():System.Void"),
                MakeSymbol(typeKey, "Type", "MyNs.MyType", "MyNs.MyType")
            ],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-1", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-1", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", "Method", ct);

        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
        Assert.Equal(2, result.TotalSymbolsA);
        Assert.Equal(2, result.TotalSymbolsB);
    }

    [Fact]
    public async Task Changes_are_sorted_by_classification_priority_then_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var existingKey = "ScheduleI:Installed:Type:Alpha";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [
                MakeSymbol(existingKey, "Type", "Alpha", "Alpha"),
                MakeSymbol("ScheduleI:Installed:Type:Zebra", "Type", "Zebra", "Zebra")
            ],
            symbolsB: [
                MakeSymbol(existingKey, "Type", "Alpha", "Alpha"),
                MakeSymbol("ScheduleI:Installed:Type:Beta", "Type", "Beta", "Beta")
            ],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-1", "declaration", "z")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-1", "declaration", "b")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(DiffClassification.Added, result.Changes[0].Classification);
        Assert.Equal("Beta", result.Changes[0].QualifiedName);
        Assert.Equal(DiffClassification.Removed, result.Changes[1].Classification);
        Assert.Equal("Zebra", result.Changes[1].QualifiedName);
    }

    // --- Helpers ---

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
