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
                "Source:field")
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
