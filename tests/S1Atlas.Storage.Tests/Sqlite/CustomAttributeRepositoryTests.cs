using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class CustomAttributeRepositoryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-custom-attributes-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly SqliteAtlasRepository _repository;

    public CustomAttributeRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task CompleteIndexRun_RoundTripsCustomAttributesOnAllTargets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var fixture = await SeedAttributeIndexAsync(cancellationToken);

        var roundTripped = await _repository.GetCompletedAttributesBySymbolIdsAsync(
            fixture.IndexId, [fixture.TypeSymbol.SymbolId, fixture.MethodSymbol.SymbolId], cancellationToken);

        Assert.Equal(
            fixture.Attributes.OrderBy(attribute => attribute.AttributeId, StringComparer.Ordinal),
            roundTripped);
        var parameter = Assert.Single(roundTripped, attribute => attribute.Target == "Parameter");
        Assert.Equal(0, parameter.ParameterIndex);
        Assert.Equal("value", parameter.ParameterName);
        Assert.All(roundTripped.Where(attribute => attribute.Target != "Parameter"), attribute =>
        {
            Assert.Null(attribute.ParameterIndex);
            Assert.Null(attribute.ParameterName);
        });
    }

    [Fact]
    public async Task GetCompletedAttributesBySymbolIds_ReturnsOnlyRequestedSymbols()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var fixture = await SeedAttributeIndexAsync(cancellationToken);

        var methodOnly = await _repository.GetCompletedAttributesBySymbolIdsAsync(
            fixture.IndexId, [fixture.MethodSymbol.SymbolId], cancellationToken);
        Assert.Equal(
            fixture.Attributes.Where(attribute => attribute.SymbolId == fixture.MethodSymbol.SymbolId).OrderBy(attribute => attribute.AttributeId, StringComparer.Ordinal),
            methodOnly);

        Assert.Empty(await _repository.GetCompletedAttributesBySymbolIdsAsync(
            fixture.IndexId, ["symbol-unknown"], cancellationToken));
        Assert.Empty(await _repository.GetCompletedAttributesBySymbolIdsAsync(
            fixture.IndexId, [], cancellationToken));
        Assert.Empty(await _repository.GetCompletedAttributesBySymbolIdsAsync(
            "index-running", [fixture.MethodSymbol.SymbolId], cancellationToken));
    }

    [Fact]
    public async Task FindCompletedAttributesByType_MatchesExactTypeCaseInsensitively()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var fixture = await SeedAttributeIndexAsync(cancellationToken);

        var rpc = await _repository.FindCompletedAttributesByTypeAsync(
            fixture.IndexId, "fishnet.object.serverrpcattribute", 10, cancellationToken);
        Assert.Equal(2, rpc.Count);
        Assert.All(rpc, attribute => Assert.Equal("FishNet.Object.ServerRpcAttribute", attribute.AttributeType));

        Assert.Empty(await _repository.FindCompletedAttributesByTypeAsync(
            fixture.IndexId, "ServerRpcAttribute", 10, cancellationToken));
        Assert.Empty(await _repository.FindCompletedAttributesByTypeAsync(
            fixture.IndexId, "System.ObsoleteAttribute", 10, cancellationToken));

        var limited = await _repository.FindCompletedAttributesByTypeAsync(
            fixture.IndexId, "FishNet.Object.ServerRpcAttribute", 1, cancellationToken);
        Assert.Single(limited);
    }

    [Fact]
    public async Task CountCompletedAttributesByType_CountsExactMatchesCaseInsensitively()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var fixture = await SeedAttributeIndexAsync(cancellationToken);

        Assert.Equal(2, await _repository.CountCompletedAttributesByTypeAsync(
            fixture.IndexId, "FishNet.Object.ServerRpcAttribute", cancellationToken));
        Assert.Equal(2, await _repository.CountCompletedAttributesByTypeAsync(
            fixture.IndexId, "FISHNET.OBJECT.SERVERRPCATTRIBUTE", cancellationToken));
        Assert.Equal(1, await _repository.CountCompletedAttributesByTypeAsync(
            fixture.IndexId, "UnityEngine.SerializeField", cancellationToken));
        Assert.Equal(0, await _repository.CountCompletedAttributesByTypeAsync(
            fixture.IndexId, "SerializeField", cancellationToken));
    }

    [Fact]
    public async Task ReadOnlyRepository_ReturnsCompletedAttributes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var fixture = await SeedAttributeIndexAsync(cancellationToken);
        var readOnly = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath));

        Assert.Equal(
            await _repository.GetCompletedAttributesBySymbolIdsAsync(
                fixture.IndexId, [fixture.TypeSymbol.SymbolId, fixture.MethodSymbol.SymbolId], cancellationToken),
            await readOnly.GetCompletedAttributesBySymbolIdsAsync(
                fixture.IndexId, [fixture.TypeSymbol.SymbolId, fixture.MethodSymbol.SymbolId], cancellationToken));
        Assert.Equal(
            await _repository.FindCompletedAttributesByTypeAsync(
                fixture.IndexId, "FishNet.Object.ServerRpcAttribute", 10, cancellationToken),
            await readOnly.FindCompletedAttributesByTypeAsync(
                fixture.IndexId, "FishNet.Object.ServerRpcAttribute", 10, cancellationToken));
        Assert.Equal(
            await _repository.CountCompletedAttributesByTypeAsync(
                fixture.IndexId, "FishNet.Object.ServerRpcAttribute", cancellationToken),
            await readOnly.CountCompletedAttributesByTypeAsync(
                fixture.IndexId, "FishNet.Object.ServerRpcAttribute", cancellationToken));
    }

    [Fact]
    public async Task CompleteIndexRun_RejectsAttributesFromAnotherSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-ownership", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-ownership", "2026-10-15T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-ownership", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbol = new IndexSymbolRecord(
            "symbol-owned", snapshot.SnapshotId, "ScheduleI:Installed:Type:Demo.Widget",
            "Type", "Demo.Widget", "Demo.Widget", false);
        var foreign = new IndexCustomAttributeRecord(
            "attribute-foreign", "snapshot-other", symbol.SymbolId, "Type",
            null, null, "UnityEngine.SerializeField", """{"type":"UnityEngine.SerializeField","fixed":[],"named":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteIndexRunAsync(
            "index-ownership",
            new IndexWriteSet([symbol], [], [], [], [], CustomAttributes: [foreign]),
            "2026-10-15T00:01:00Z",
            cancellationToken));
    }

    private async Task<AttributeFixture> SeedAttributeIndexAsync(CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-attributes", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-attributes", "2026-10-15T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-attributes", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var typeSymbol = new IndexSymbolRecord(
            "symbol-type", snapshot.SnapshotId, "ScheduleI:Installed:Type:Demo.Widget",
            "Type", "Demo.Widget", "Demo.Widget", false);
        var methodSymbol = new IndexSymbolRecord(
            "symbol-method", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Run()",
            "Method", "Demo.Widget.Run", "System.Void Demo.Widget::Run()", false);
        var attributes = new[]
        {
            new IndexCustomAttributeRecord(
                "attribute-type", snapshot.SnapshotId, typeSymbol.SymbolId, "Type",
                null, null, "System.SerializableAttribute", """{"type":"System.SerializableAttribute","fixed":[],"named":[]}"""),
            new IndexCustomAttributeRecord(
                "attribute-member", snapshot.SnapshotId, methodSymbol.SymbolId, "Member",
                null, null, "FishNet.Object.ServerRpcAttribute", """{"type":"FishNet.Object.ServerRpcAttribute","fixed":[],"named":[]}"""),
            new IndexCustomAttributeRecord(
                "attribute-parameter", snapshot.SnapshotId, methodSymbol.SymbolId, "Parameter",
                0, "value", "FishNet.Object.ServerRpcAttribute", """{"type":"FishNet.Object.ServerRpcAttribute","fixed":[],"named":[]}"""),
            new IndexCustomAttributeRecord(
                "attribute-field", snapshot.SnapshotId, typeSymbol.SymbolId, "Member",
                null, null, "UnityEngine.SerializeField", """{"type":"UnityEngine.SerializeField","fixed":[],"named":[]}"""),
        };
        await _repository.CompleteIndexRunAsync(
            "index-attributes",
            new IndexWriteSet([typeSymbol, methodSymbol], [], [], [], [], CustomAttributes: attributes),
            "2026-10-15T00:01:00Z",
            cancellationToken);

        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-running", snapshot.SnapshotId, IndexRunStatus.Running, "2026-10-15T00:02:00Z"),
            cancellationToken);

        return new AttributeFixture("index-attributes", typeSymbol, methodSymbol, attributes);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed record AttributeFixture(
        string IndexId,
        IndexSymbolRecord TypeSymbol,
        IndexSymbolRecord MethodSymbol,
        IReadOnlyList<IndexCustomAttributeRecord> Attributes);
}
