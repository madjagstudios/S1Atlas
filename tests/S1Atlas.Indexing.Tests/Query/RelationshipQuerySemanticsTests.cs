using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class RelationshipQuerySemanticsTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-relationship-semantics-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public RelationshipQuerySemanticsTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Refs_returns_all_incoming_and_outgoing_relationship_kinds_with_enriched_endpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.RefsAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(
            ["incoming-call", "incoming-construct", "outgoing-call", "outgoing-construct", "outgoing-field", "outgoing-parameter", "outgoing-unresolved-call"],
            result.Relationships.Select(edge => edge.RelationshipId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains(result.Relationships, edge => edge.Direction == "Incoming");
        Assert.Contains(result.Relationships, edge => edge.Direction == "Outgoing");

        var unresolved = Assert.Single(result.Relationships, edge => edge.RelationshipId == "outgoing-unresolved-call");
        Assert.True(unresolved.Source.Resolved);
        Assert.Equal(fixture.Selected.SymbolId, unresolved.Source.SymbolId);
        Assert.False(unresolved.Target.Resolved);
        Assert.Null(unresolved.Target.SymbolId);
        Assert.Equal("External.Missing::Run()", unresolved.Target.RawText);
    }

    [Fact]
    public async Task Callers_returns_only_incoming_calls_and_constructs_targeting_the_selected_symbol()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CallersAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            cancellationToken);

        Assert.Equal(["incoming-call", "incoming-construct"], result.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(result.Relationships, edge => Assert.Equal("Incoming", edge.Direction));
        Assert.All(result.Relationships, edge => Assert.Contains(edge.Kind, new[] { "Calls", "Constructs" }));
        Assert.All(result.Relationships, edge => Assert.True(edge.Source.Resolved));
        Assert.All(result.Relationships, edge => Assert.True(edge.Target.Resolved));
        Assert.Equal(BodyRecoveryStatus.Unknown, result.BodyRecoveryStatus);
        Assert.True(result.CallerCompletenessBoundedByTargetResolution);
        Assert.NotEmpty(result.CompletenessNotice);
    }

    [Fact]
    public async Task Callees_returns_only_outgoing_calls_and_constructs_and_preserves_unresolved_target_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CalleesAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            cancellationToken);

        Assert.Equal(
            ["outgoing-call", "outgoing-construct", "outgoing-unresolved-call"],
            result.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(result.Relationships, edge => Assert.Equal("Outgoing", edge.Direction));
        Assert.All(result.Relationships, edge => Assert.Contains(edge.Kind, new[] { "Calls", "Constructs" }));
        var unresolved = Assert.Single(result.Relationships, edge => edge.RelationshipId == "outgoing-unresolved-call");
        Assert.False(unresolved.Target.Resolved);
        Assert.Equal("External.Missing::Run()", unresolved.Target.RawText);
        Assert.Equal(BodyRecoveryStatus.Unknown, result.BodyRecoveryStatus);
        Assert.False(result.CallerCompletenessBoundedByTargetResolution);
        Assert.NotEmpty(result.CompletenessNotice);
    }

    [Fact]
    public async Task Relationship_queries_return_structured_ambiguity_without_loading_edges_for_a_chosen_candidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.RefsAsync(
            "worker",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Equal(2, result.Resolution.Candidates.Count);
        Assert.Empty(result.Relationships);
    }

    private async Task<RelationshipFixture> SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-query-relationships",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-query-relationships",
            "2026-08-14T07:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-query-relationships",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var selected = Method("selected", "Demo.Target.Run", snapshot.SnapshotId, BodyRecoveryStatus.Unknown);
        var caller = Method("caller", "Demo.Caller.Invoke", snapshot.SnapshotId, BodyRecoveryStatus.Recovered);
        var constructorCaller = Method("constructor-caller", "Demo.Factory.Build", snapshot.SnapshotId, BodyRecoveryStatus.Recovered);
        var callee = Method("callee", "Demo.Service.Execute", snapshot.SnapshotId, BodyRecoveryStatus.Recovered);
        var constructed = new IndexSymbolRecord(
            "constructed",
            snapshot.SnapshotId,
            "ScheduleI:Installed:Constructor:Demo.Item::.ctor()",
            "Constructor",
            "Demo.Item..ctor",
            "System.Void Demo.Item::.ctor()",
            false,
            BodyRecoveryStatus.Recovered);
        var field = new IndexSymbolRecord(
            "field",
            snapshot.SnapshotId,
            "ScheduleI:Installed:Field:Demo.State::System.Int32 Value",
            "Field",
            "Demo.State.Value",
            "System.Int32 Value",
            false);
        var parameterType = new IndexSymbolRecord(
            "parameter-type",
            snapshot.SnapshotId,
            "ScheduleI:Installed:Type:Demo.Payload",
            "Type",
            "Demo.Payload",
            "Demo.Payload",
            false);
        var workerA = Method("worker-a", "Alpha.Worker.Run", snapshot.SnapshotId, BodyRecoveryStatus.Recovered);
        var workerB = Method("worker-b", "Beta.Worker.Run", snapshot.SnapshotId, BodyRecoveryStatus.Recovered);

        var relationships = new[]
        {
            Edge("incoming-call", caller.SymbolId, selected.SymbolId, null, "Calls", snapshot.SnapshotId),
            Edge("incoming-construct", constructorCaller.SymbolId, selected.SymbolId, null, "Constructs", snapshot.SnapshotId),
            Edge("outgoing-call", selected.SymbolId, callee.SymbolId, null, "Calls", snapshot.SnapshotId),
            Edge("outgoing-construct", selected.SymbolId, constructed.SymbolId, null, "Constructs", snapshot.SnapshotId),
            Edge("outgoing-unresolved-call", selected.SymbolId, null, "External.Missing::Run()", "Calls", snapshot.SnapshotId),
            Edge("outgoing-field", selected.SymbolId, field.SymbolId, null, "ReadsField", snapshot.SnapshotId),
            Edge("outgoing-parameter", selected.SymbolId, parameterType.SymbolId, null, "ParameterType", snapshot.SnapshotId)
        };

        await _repository.CompleteIndexRunAsync(
            "index-query-relationships",
            new IndexWriteSet(
                [selected, caller, constructorCaller, callee, constructed, field, parameterType, workerA, workerB],
                [],
                [],
                [],
                relationships),
            "2026-08-14T07:01:00Z",
            cancellationToken);
        return new RelationshipFixture(selected);
    }

    private static IndexSymbolRecord Method(
        string id,
        string qualifiedName,
        string snapshotId,
        BodyRecoveryStatus status) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + qualifiedName,
            "Method",
            qualifiedName,
            "System.Void " + qualifiedName + "()",
            false,
            status);

    private static IndexRelationshipRecord Edge(
        string id,
        string source,
        string? target,
        string? targetText,
        string kind,
        string snapshotId) =>
        new(id, snapshotId, source, target, targetText, kind, "fixture:" + id);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record RelationshipFixture(IndexSymbolRecord Selected);
}
