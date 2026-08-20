using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class IndexQueryBulkTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-query-bulk-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public IndexQueryBulkTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Bulk_queries_extract_namespaces_select_latest_completed_api_and_measure_relationship_totals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);

        var old = await SeedApiRunAsync(
            "snapshot-api-old",
            "index-api-old",
            "2026-08-20T00:00:00Z",
            "2026-08-20T00:01:00Z",
            includeRelationship: false,
            cancellationToken);
        var current = await SeedApiRunAsync(
            "snapshot-api-current",
            "index-api-current",
            "2026-08-20T00:02:00Z",
            "2026-08-20T00:03:00Z",
            includeRelationship: true,
            cancellationToken);
        _ = old;

        var service = new IndexQueryService(_repository);
        var namespaces = await service.ListNamespacesInIndexAsync(
            current.Run,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            cancellationToken);
        Assert.Equal(["Alpha", "Beta"], namespaces.Namespaces);
        Assert.Equal(2, namespaces.TotalCount);

        var page = await service.ListSymbolsInIndexAsync(
            current.Run,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            new IndexPageRequest(0, 2),
            cancellationToken);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(2, page.Results.Count);
        Assert.True(page.HasMore);

        var selected = await service.GetLatestCompletedIndexSelectionAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            cancellationToken);
        Assert.NotNull(selected);
        Assert.Equal("index-api-current", selected.Run.IndexId);
        Assert.Equal("s1api:release:current", selected.Snapshot.SourceIdentity);

        var evidence = await service.GetRelationshipEvidenceInIndexAsync(
            current.Run,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "index-api-current-target",
            cancellationToken);
        Assert.Equal(4, evidence.ReferenceTotal);
        Assert.Equal(1, evidence.CallerTotal);
        Assert.Equal(1, evidence.CalleeTotal);
        Assert.Equal(4, evidence.References.Count);
        Assert.Single(evidence.Callers);
        Assert.Single(evidence.Callees);
    }

    private async Task<SeededRun> SeedApiRunAsync(
        string snapshotId,
        string indexId,
        string createdAt,
        string completedAt,
        bool includeRelationship,
        CancellationToken cancellationToken)
    {
        var sourceIdentity = "s1api:release:" + (indexId.EndsWith("old", StringComparison.Ordinal) ? "old" : "current");
        var snapshot = new CodeSnapshotRecord(snapshotId, CodebaseKind.S1Api, CodeChannel.Release, sourceIdentity, createdAt);
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, createdAt), cancellationToken);
        var symbols = new[]
        {
            Symbol(indexId + "-target", snapshotId, "Alpha.Target", "Type"),
            Symbol(indexId + "-caller", snapshotId, "Beta.Caller.Run", "Method"),
            Symbol(indexId + "-callee", snapshotId, "Beta.Callee.Run", "Method"),
            Symbol(indexId + "-field", snapshotId, "Alpha.Target.Value", "Field")
        };
        var targetId = indexId + "-target";
        var callerId = indexId + "-caller";
        var calleeId = indexId + "-callee";
        var fieldId = indexId + "-field";
        var relationships = includeRelationship
            ? new[]
            {
                Edge(indexId + "-ref-in", snapshotId, callerId, targetId, "ReadsField"),
                Edge(indexId + "-ref-out", snapshotId, targetId, fieldId, "ReadsField"),
                Edge(indexId + "-call-in", snapshotId, callerId, targetId, "Calls"),
                Edge(indexId + "-call-out", snapshotId, targetId, calleeId, "Calls")
            }
            : [];
        await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, [], [], [], relationships), completedAt, cancellationToken);
        return new SeededRun(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Completed, createdAt, completedAt), snapshot);
    }

    private static IndexSymbolRecord Symbol(string id, string snapshotId, string qualifiedName, string kind) =>
        new(id, snapshotId, "S1Api:Release:" + kind + ":" + CanonicalName(qualifiedName, kind), kind, qualifiedName, qualifiedName, false, kind == "Method" ? BodyRecoveryStatus.Recovered : null);

    private static string CanonicalName(string qualifiedName, string kind)
    {
        if (kind == "Type") return qualifiedName;
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0 ? qualifiedName : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }

    private static IndexRelationshipRecord Edge(string id, string snapshotId, string source, string target, string kind) =>
        new(id, snapshotId, source, target, null, kind, "fixture:" + id);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record SeededRun(IndexRunRecord Run, CodeSnapshotRecord Snapshot);
}
