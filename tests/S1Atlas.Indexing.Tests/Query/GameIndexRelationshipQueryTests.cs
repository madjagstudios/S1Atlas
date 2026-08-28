using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using System.Reflection;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class GameIndexRelationshipQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-game-index-relationship-query-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public GameIndexRelationshipQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task CallSitesInIndexAsync_matches_unresolved_and_resolved_engine_targets_across_overloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CallSitesInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            10,
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(["callsite-001", "callsite-002", "callsite-003"], result.Relationships.Select(edge => edge.RelationshipId));

        var unresolved = Assert.Single(result.Relationships, edge => edge.RelationshipId == "callsite-001");
        Assert.True(unresolved.Source.Resolved);
        Assert.False(unresolved.Target.Resolved);
        Assert.Equal("UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", unresolved.Target.RawText);

        var resolved = Assert.Single(result.Relationships, edge => edge.RelationshipId == "callsite-002");
        Assert.True(resolved.Target.Resolved);
        Assert.Equal("engine-complete-off-mesh-link", resolved.Target.SymbolId);
    }

    [Fact]
    public async Task CallSitesInIndexAsync_matches_unresolved_explicit_parameters_with_canonical_return_type_without_crossing_overloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CallSitesInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "External.Bcl.Widget.Convert(System.String)",
            10,
            cancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(["callsite-external-string"], result.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(
            "External.Bcl.Widget::Convert(System.String):System.Int32",
            Assert.Single(result.Relationships).Target.RawText);
    }

    [Fact]
    public async Task CallSitesAsync_applies_exact_selector_limits_in_relationship_id_order_and_reports_static_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 1),
            cancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal(["callsite-001"], result.Relationships.Select(edge => edge.RelationshipId));
        Assert.Contains("recovered IL references", result.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not prove runtime behavior", result.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution order", result.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallSitesAsync_returns_an_empty_page_when_no_matches_exist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent.Missing",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 5),
            cancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public async Task CallSitesAsync_reads_bounded_pages_across_channels_and_merges_full_totals_deterministically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedApiCallSiteChannelsAsync(cancellationToken);
        var proxy = DispatchProxy.Create<IIndexRepository, RecordingIndexRepositoryProxy>();
        var recorder = (RecordingIndexRepositoryProxy)(object)proxy;
        recorder.Inner = _repository;
        var service = new IndexQueryService(proxy);

        var result = await service.CallSitesAsync(
            "External.Bcl.Widget.Convert(System.String)",
            new IndexQueryOptions(CodebaseKind.S1Api, AllChannels: true, Limit: 2),
            cancellationToken);

        Assert.Equal(9, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(["callsite-api-a-installed", "callsite-api-a-preview"], result.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(3, recorder.TargetTextReadLimits.Count);
        Assert.All(recorder.TargetTextReadLimits, limit => Assert.Equal(2, limit));
    }

    [Fact]
    public async Task FieldReferencesAsync_returns_resolved_relationships_with_game_provenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FieldReferencesAsync(
            "Demo.State.Value",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            FieldReferenceFilter.All,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(["field-read-001", "field-read-002", "field-write-001"], result.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(result.Relationships, edge => Assert.Equal("Incoming", edge.Direction));
        Assert.All(result.Relationships, edge => Assert.Equal("game", edge.Source.Origin));
        Assert.All(result.Relationships, edge => Assert.Equal("game", edge.Target.Origin));
        Assert.All(result.Relationships, edge => Assert.Equal("state-field", edge.Target.SymbolId));
        Assert.All(result.Relationships, edge => Assert.Equal("Demo.State.Value", edge.Target.QualifiedName));
        Assert.All(result.Relationships, edge => Assert.Equal("System.Int32 Demo.State::Value", edge.Target.Signature));
    }

    [Fact]
    public async Task FieldReferencesInIndexAsync_applies_reader_and_writer_filters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var readers = await service.FieldReferencesInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "System.Int32 Demo.State::Value",
            10,
            FieldReferenceFilter.Readers,
            cancellationToken);
        var writers = await service.FieldReferencesInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "System.Int32 Demo.State::Value",
            10,
            FieldReferenceFilter.Writers,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, readers.Resolution.Status);
        Assert.Equal(2, readers.TotalCount);
        Assert.Equal(["field-read-001", "field-read-002"], readers.Relationships.Select(edge => edge.RelationshipId));

        Assert.Equal(SymbolResolutionStatus.Resolved, writers.Resolution.Status);
        Assert.Equal(1, writers.TotalCount);
        Assert.Equal(["field-write-001"], writers.Relationships.Select(edge => edge.RelationshipId));
    }

    [Fact]
    public async Task FieldReferencesInIndexAsync_returns_ambiguity_without_loading_edges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FieldReferencesInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "SharedValue",
            10,
            FieldReferenceFilter.All,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Equal(2, result.Resolution.Candidates.Count);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public async Task FieldReferencesAsync_returns_not_found_when_the_selected_field_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FieldReferencesAsync(
            "Demo.State.Missing",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            FieldReferenceFilter.All,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Resolution.Status);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Relationships);
    }

    private async Task<QueryFixture> SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-game-index-relationships",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-game-index-relationships",
            "2026-08-28T16:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-game-index-relationships",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            Method("engine-complete-off-mesh-link", snapshot.SnapshotId, "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink"),
            Method("callsite-source-a", snapshot.SnapshotId, "Demo.CallsiteA.Run"),
            Method("callsite-source-b", snapshot.SnapshotId, "Demo.CallsiteB.Run"),
            Method("field-source-a", snapshot.SnapshotId, "Demo.FieldReaderA.Run"),
            Method("field-source-b", snapshot.SnapshotId, "Demo.FieldReaderB.Run"),
            Method("field-source-c", snapshot.SnapshotId, "Demo.FieldWriter.Run"),
            Field("state-field", snapshot.SnapshotId, "Demo.State.Value"),
            Field("ambiguous-field-a", snapshot.SnapshotId, "Alpha.State.SharedValue"),
            Field("ambiguous-field-b", snapshot.SnapshotId, "Beta.State.SharedValue")
        };

        var relationships = new[]
        {
            Edge("callsite-001", snapshot.SnapshotId, "callsite-source-a", null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Calls", "RecoveredIL"),
            Edge("callsite-002", snapshot.SnapshotId, "callsite-source-b", "engine-complete-off-mesh-link", "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Calls", "RecoveredIL"),
            Edge("callsite-003", snapshot.SnapshotId, "callsite-source-a", null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink(System.Boolean)", "Calls", "RecoveredIL"),
            Edge("callsite-004", snapshot.SnapshotId, "callsite-source-a", null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLinkLater()", "Calls", "RecoveredIL"),
            Edge("callsite-005", snapshot.SnapshotId, "callsite-source-a", null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Constructs", "RecoveredIL"),
            Edge("callsite-external-string", snapshot.SnapshotId, "callsite-source-a", null, "External.Bcl.Widget::Convert(System.String):System.Int32", "Calls", "RecoveredIL"),
            Edge("callsite-external-int", snapshot.SnapshotId, "callsite-source-a", null, "External.Bcl.Widget::Convert(System.Int32):System.Int32", "Calls", "RecoveredIL"),
            Edge("field-read-001", snapshot.SnapshotId, "field-source-a", "state-field", "Demo.State::Value", "ReadsField", "RecoveredIL"),
            Edge("field-read-002", snapshot.SnapshotId, "field-source-b", "state-field", "Demo.State::Value", "ReadsField", "RecoveredIL"),
            Edge("field-write-001", snapshot.SnapshotId, "field-source-c", "state-field", "Demo.State::Value", "WritesField", "RecoveredIL"),
            Edge("field-parameter-001", snapshot.SnapshotId, "field-source-c", "state-field", "Demo.State::Value", "ParameterType", "Metadata")
        };

        await _repository.CompleteIndexRunAsync(
            "index-game-index-relationships",
            new IndexWriteSet(symbols, [], [], [], relationships),
            "2026-08-28T16:01:00Z",
            cancellationToken);

        return new QueryFixture(
            new IndexRunRecord(
                "index-game-index-relationships",
                snapshot.SnapshotId,
                IndexRunStatus.Completed,
                snapshot.CreatedAtUtc,
                "2026-08-28T16:01:00Z"));
    }

    private async Task<QueryFixture> SeedApiCallSiteChannelsAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        foreach (var channel in new[] { CodeChannel.Installed, CodeChannel.Release, CodeChannel.Preview })
        {
            var channelName = channel.ToString().ToLowerInvariant();
            var snapshot = new CodeSnapshotRecord(
                "snapshot-api-call-sites-" + channelName,
                CodebaseKind.S1Api,
                channel,
                "s1api:" + channelName + ":call-sites",
                "2026-08-28T17:00:00Z");
            await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
            var indexId = "index-api-call-sites-" + channelName;
            await _repository.StartIndexRunAsync(
                new IndexRunRecord(indexId, snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
                cancellationToken);

            var source = Method(
                "source-api-" + channelName,
                snapshot.SnapshotId,
                "ApiCaller." + channelName + ".Run");
            var relationships = new[]
            {
                Edge("callsite-api-a-" + channelName, snapshot.SnapshotId, source.SymbolId, null, "External.Bcl.Widget::Convert(System.String)", "Calls", "RecoveredIL"),
                Edge("callsite-api-b-" + channelName, snapshot.SnapshotId, source.SymbolId, null, "External.Bcl.Widget::Convert(System.String)", "Calls", "RecoveredIL"),
                Edge("callsite-api-c-" + channelName, snapshot.SnapshotId, source.SymbolId, null, "External.Bcl.Widget::Convert(System.String)", "Calls", "RecoveredIL")
            };
            await _repository.CompleteIndexRunAsync(
                indexId,
                new IndexWriteSet([source], [], [], [], relationships),
                "2026-08-28T17:01:00Z",
                cancellationToken);
        }

        return new QueryFixture(
            new IndexRunRecord(
                "index-api-call-sites-installed",
                "snapshot-api-call-sites-installed",
                IndexRunStatus.Completed,
                "2026-08-28T17:00:00Z",
                "2026-08-28T17:01:00Z"));
    }

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + CanonicalMember(qualifiedName),
            "Method",
            qualifiedName,
            "System.Void " + CanonicalMember(qualifiedName) + "()",
            false,
            BodyRecoveryStatus.Recovered);

    private static IndexSymbolRecord Field(string id, string snapshotId, string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        var typeName = qualifiedName[..separator];
        var fieldName = qualifiedName[(separator + 1)..];
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ScheduleI:Installed:Field:" + typeName + "::System.Int32 " + fieldName,
            "Field",
            qualifiedName,
            "System.Int32 " + typeName + "::" + fieldName,
            false);
    }

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }

    private static IndexRelationshipRecord Edge(
        string id,
        string snapshotId,
        string source,
        string? target,
        string? targetText,
        string kind,
        string evidence) =>
        new(id, snapshotId, source, target, targetText, kind, evidence);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record QueryFixture(IndexRunRecord Run);

    private class RecordingIndexRepositoryProxy : DispatchProxy
    {
        public IIndexRepository Inner { get; set; } = null!;
        public List<int> TargetTextReadLimits { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IIndexRepository.GetCompletedRelationshipsByTargetTextAsync) && args?[4] is int limit)
                TargetTextReadLimits.Add(limit);
            return targetMethod!.Invoke(Inner, args);
        }
    }
}
