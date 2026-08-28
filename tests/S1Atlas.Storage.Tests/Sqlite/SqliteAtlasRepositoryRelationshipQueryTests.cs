using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryRelationshipQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-relationship-query-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SqliteAtlasRepositoryRelationshipQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Completed_relationship_queries_filter_by_exact_source_and_target_symbol_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
            "index-relationships",
            "source",
            cancellationToken);
        var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
            "index-relationships",
            "target",
            cancellationToken);

        Assert.Equal(["call-resolved", "call-unresolved"], outgoing.Select(edge => edge.RelationshipId));
        Assert.Equal(["call-resolved", "construct-incoming"], incoming.Select(edge => edge.RelationshipId));
        Assert.DoesNotContain(outgoing, edge => edge.RelationshipId == "field-read");
        Assert.DoesNotContain(incoming, edge => edge.RelationshipId == "call-unresolved");
    }

    [Fact]
    public async Task Completed_call_site_queries_match_exact_target_text_and_preserve_unresolved_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        var total = await _repository.CountCompletedRelationshipsByTargetTextAsync(
            "index-relationships",
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
            RelationshipTargetTextMatchMode.Exact,
            "Calls",
            cancellationToken);
        var results = await _repository.GetCompletedRelationshipsByTargetTextAsync(
            "index-relationships",
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
            RelationshipTargetTextMatchMode.Exact,
            "Calls",
            10,
            cancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(["callsite-001", "callsite-002"], results.Select(edge => edge.RelationshipId));
        Assert.Equal(
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
            Assert.Single(results, edge => edge.RelationshipId == "callsite-001").TargetText);
        Assert.Equal(
            "engine-complete-off-mesh-link",
            Assert.Single(results, edge => edge.RelationshipId == "callsite-002").TargetSymbolId);
    }

    [Fact]
    public async Task Completed_call_site_queries_match_prefixes_precisely_and_apply_deterministic_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        var total = await _repository.CountCompletedRelationshipsByTargetTextAsync(
            "index-relationships",
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink",
            RelationshipTargetTextMatchMode.Prefix,
            "Calls",
            cancellationToken);
        var results = await _repository.GetCompletedRelationshipsByTargetTextAsync(
            "index-relationships",
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink",
            RelationshipTargetTextMatchMode.Prefix,
            "Calls",
            2,
            cancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(["callsite-001", "callsite-002"], results.Select(edge => edge.RelationshipId));
        Assert.DoesNotContain(results, edge => edge.RelationshipId == "callsite-003");
        Assert.DoesNotContain(results, edge => edge.RelationshipId == "callsite-004");
    }

    [Fact]
    public async Task Completed_field_relationship_queries_filter_by_kind_and_limit_results()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        var readCount = await _repository.CountCompletedRelationshipsByTargetSymbolIdAsync(
            "index-relationships",
            "state-field",
            "ReadsField",
            cancellationToken);
        var readResults = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
            "index-relationships",
            "state-field",
            "ReadsField",
            1,
            cancellationToken);
        var writeCount = await _repository.CountCompletedRelationshipsByTargetSymbolIdAsync(
            "index-relationships",
            "state-field",
            "WritesField",
            cancellationToken);
        var writeResults = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
            "index-relationships",
            "state-field",
            "WritesField",
            10,
            cancellationToken);

        Assert.Equal(2, readCount);
        Assert.Equal(["field-read-001"], readResults.Select(edge => edge.RelationshipId));
        Assert.Equal(1, writeCount);
        Assert.Equal(["field-write-001"], writeResults.Select(edge => edge.RelationshipId));
    }

    [Fact]
    public async Task Completed_symbol_batch_lookup_returns_only_requested_existing_symbols_deterministically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        var symbols = await _repository.GetCompletedSymbolsByIdsAsync(
            "index-relationships",
            ["target", "missing", "source", "target"],
            cancellationToken);

        Assert.Equal(["source", "target"], symbols.Select(symbol => symbol.SymbolId));
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-relationships",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-relationships",
            "2026-08-14T06:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-relationships",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            new IndexSymbolRecord(
                "source",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.Source::Run()",
                "Method",
                "Demo.Source.Run",
                "System.Void Demo.Source::Run()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "target",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.Target::Execute()",
                "Method",
                "Demo.Target.Execute",
                "System.Void Demo.Target::Execute()",
                false,
                BodyRecoveryStatus.Unknown),
            new IndexSymbolRecord(
                "other",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.Other::Build()",
                "Method",
                "Demo.Other.Build",
                "System.Void Demo.Other::Build()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "engine-complete-off-mesh-link",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                "Method",
                "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "state-field",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Field:Demo.State::System.Int32 Value",
                "Field",
                "Demo.State.Value",
                "System.Int32 Demo.State::Value",
                false),
            new IndexSymbolRecord(
                "callsite-source-a",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.CallsiteA::Run()",
                "Method",
                "Demo.CallsiteA.Run",
                "System.Void Demo.CallsiteA::Run()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "callsite-source-b",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.CallsiteB::Run()",
                "Method",
                "Demo.CallsiteB.Run",
                "System.Void Demo.CallsiteB::Run()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "field-source-a",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.FieldReaderA::Run()",
                "Method",
                "Demo.FieldReaderA.Run",
                "System.Void Demo.FieldReaderA::Run()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "field-source-b",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.FieldReaderB::Run()",
                "Method",
                "Demo.FieldReaderB.Run",
                "System.Void Demo.FieldReaderB::Run()",
                false,
                BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord(
                "field-source-c",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.FieldWriter::Run()",
                "Method",
                "Demo.FieldWriter.Run",
                "System.Void Demo.FieldWriter::Run()",
                false,
                BodyRecoveryStatus.Recovered)
        };
        var relationships = new[]
        {
            new IndexRelationshipRecord(
                "call-resolved",
                snapshot.SnapshotId,
                "source",
                "target",
                null,
                "Calls",
                "IL:call"),
            new IndexRelationshipRecord(
                "call-unresolved",
                snapshot.SnapshotId,
                "source",
                null,
                "External.Missing::Execute()",
                "Calls",
                "IL:call"),
            new IndexRelationshipRecord(
                "construct-incoming",
                snapshot.SnapshotId,
                "other",
                "target",
                null,
                "Constructs",
                "IL:newobj"),
            new IndexRelationshipRecord(
                "field-read",
                snapshot.SnapshotId,
                "target",
                "source",
                null,
                "ReadsField",
                "Source:field"),
            new IndexRelationshipRecord(
                "callsite-001",
                snapshot.SnapshotId,
                "callsite-source-a",
                null,
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                "Calls",
                "IL:call"),
            new IndexRelationshipRecord(
                "callsite-002",
                snapshot.SnapshotId,
                "callsite-source-b",
                "engine-complete-off-mesh-link",
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                "Calls",
                "IL:callvirt"),
            new IndexRelationshipRecord(
                "callsite-003",
                snapshot.SnapshotId,
                "callsite-source-a",
                null,
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink(System.Boolean)",
                "Calls",
                "IL:call"),
            new IndexRelationshipRecord(
                "callsite-004",
                snapshot.SnapshotId,
                "callsite-source-a",
                null,
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLinkLater()",
                "Calls",
                "IL:call"),
            new IndexRelationshipRecord(
                "callsite-005",
                snapshot.SnapshotId,
                "callsite-source-a",
                null,
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                "Constructs",
                "IL:newobj"),
            new IndexRelationshipRecord(
                "field-read-001",
                snapshot.SnapshotId,
                "field-source-a",
                "state-field",
                "Demo.State::Value",
                "ReadsField",
                "IL:ldfld"),
            new IndexRelationshipRecord(
                "field-read-002",
                snapshot.SnapshotId,
                "field-source-b",
                "state-field",
                "Demo.State::Value",
                "ReadsField",
                "IL:ldsfld"),
            new IndexRelationshipRecord(
                "field-write-001",
                snapshot.SnapshotId,
                "field-source-c",
                "state-field",
                "Demo.State::Value",
                "WritesField",
                "IL:stfld"),
            new IndexRelationshipRecord(
                "field-parameter-001",
                snapshot.SnapshotId,
                "field-source-c",
                "state-field",
                "Demo.State::Value",
                "ParameterType",
                "IL:param")
        };

        await _repository.CompleteIndexRunAsync(
            "index-relationships",
            new IndexWriteSet(symbols, [], [], [], relationships),
            "2026-08-14T06:01:00Z",
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
