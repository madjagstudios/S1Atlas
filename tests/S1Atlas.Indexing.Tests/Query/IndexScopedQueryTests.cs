using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class IndexScopedQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-index-scoped-query-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public IndexScopedQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task SearchInIndex_targets_the_supplied_run_instead_of_the_latest_completed_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await WithTwoCompletedInstalledIndexesAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchInIndexAsync(
            fixture.OlderRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Alpha",
            limit: 50,
            kind: null,
            cancellationToken);

        Assert.Null(result.ResolutionStatus);
        var symbol = Assert.Single(result.Results);
        Assert.Contains("Alpha", symbol.QualifiedName, StringComparison.Ordinal);
        Assert.All(result.Results, item => Assert.Equal(fixture.OlderRun.IndexId, item.IndexId));
    }

    [Fact]
    public async Task SourceInIndex_verifies_hash_and_returns_the_selected_snippet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await WithTwoCompletedInstalledIndexesAsync(cancellationToken);
        var service = new IndexQueryService(_repository, fixture.DataRoot);

        var result = await service.SourceInIndexAsync(
            fixture.OlderRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            fixture.Selected.SymbolId,
            context: 0,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        var snippet = Assert.IsType<SourceSnippetQueryResult>(result.Snippet);
        Assert.Equal(fixture.Selected.SymbolId, snippet.Symbol.SymbolId);
        Assert.Equal(fixture.OlderRun.IndexId, snippet.IndexId);
        Assert.Equal(fixture.SourceFile.RelativePath, snippet.RelativePath);
        Assert.Equal(fixture.SourceFile.Sha256, snippet.Sha256);
        Assert.Equal(fixture.SourceFile.ByteCount, snippet.ByteCount);
        Assert.Equal(fixture.Location.StartLine, snippet.Location.StartLine);
        Assert.Equal(fixture.Location.StartColumn, snippet.Location.StartColumn);
        Assert.Equal("public void Run() { }", snippet.Text);
    }

    [Fact]
    public async Task SourceInIndex_throws_when_the_selected_source_file_is_tampered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await WithTwoCompletedInstalledIndexesAsync(cancellationToken);
        var service = new IndexQueryService(_repository, fixture.DataRoot);

        await File.WriteAllTextAsync(fixture.SourcePath, "tampered source bytes", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SourceInIndexAsync(
            fixture.OlderRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            fixture.Selected.SymbolId,
            context: 0,
            cancellationToken));
    }

    [Fact]
    public async Task RefsInIndex_returns_edges_from_the_supplied_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await WithTwoCompletedInstalledIndexesAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.RefsInIndexAsync(
            fixture.OlderRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            fixture.Selected.SymbolId,
            limit: 50,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(fixture.Selected.SymbolId, result.Resolution.Symbol?.SymbolId);
        Assert.Equal(fixture.OlderRun.IndexId, result.Resolution.Symbol?.IndexId);
        Assert.Contains(result.Relationships, edge => edge.RelationshipId == "older-incoming-call");
        Assert.Contains(result.Relationships, edge => edge.RelationshipId == "older-outgoing-call");
    }

    private async Task<ScopedIndexFixture> WithTwoCompletedInstalledIndexesAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);

        var olderSnapshot = new CodeSnapshotRecord(
            "snapshot-older",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-older",
            "2026-08-14T04:00:00Z");
        await _repository.CreateCodeSnapshotAsync(olderSnapshot, cancellationToken);
        var olderRun = new IndexRunRecord(
            "index-older",
            olderSnapshot.SnapshotId,
            IndexRunStatus.Running,
            olderSnapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(olderRun, cancellationToken);

        var selected = new IndexSymbolRecord(
            "older-method",
            olderSnapshot.SnapshotId,
            "ScheduleI:Installed:Method:Demo.Widget::Run()",
            "Method",
            "Demo.Widget.Run",
            "System.Void Demo.Widget::Run()",
            false,
            BodyRecoveryStatus.Recovered);
        var incomingCaller = new IndexSymbolRecord(
            "older-caller",
            olderSnapshot.SnapshotId,
            "ScheduleI:Installed:Method:Demo.Caller::Invoke()",
            "Method",
            "Demo.Caller.Invoke",
            "System.Void Demo.Caller::Invoke()",
            false,
            BodyRecoveryStatus.Recovered);
        var outgoingCallee = new IndexSymbolRecord(
            "older-callee",
            olderSnapshot.SnapshotId,
            "ScheduleI:Installed:Method:Demo.Service::Execute()",
            "Method",
            "Demo.Service.Execute",
            "System.Void Demo.Service::Execute()",
            false,
            BodyRecoveryStatus.Recovered);
        var alpha = new IndexSymbolRecord(
            "older-alpha",
            olderSnapshot.SnapshotId,
            "ScheduleI:Installed:Type:Alpha.Widget",
            "Type",
            "Alpha.Widget",
            "Alpha.Widget",
            false);

        const string sourceText = "namespace Demo;\npublic class Widget\n{\n    public void Run() { }\n}\n";
        var sourceFile = new IndexSourceFileRecord(
            "older-source-file",
            olderSnapshot.SnapshotId,
            "Assembly-CSharp.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var location = new IndexSourceLocationRecord(
            selected.SymbolId,
            sourceFile.SourceFileId,
            4,
            5,
            4,
            26);

        var indexRoot = Path.Combine(dataRoot, "builds", "build-older", "indexes", olderRun.IndexId);
        Directory.CreateDirectory(indexRoot);
        var selectedPath = Path.Combine(indexRoot, sourceFile.RelativePath);
        await File.WriteAllTextAsync(selectedPath, sourceText, new UTF8Encoding(false), cancellationToken);

        await _repository.CompleteIndexRunAsync(
            olderRun.IndexId,
            new IndexWriteSet(
                [alpha, selected, incomingCaller, outgoingCallee],
                [sourceFile],
                [location],
                [],
                [
                    new IndexRelationshipRecord(
                        "older-incoming-call",
                        olderSnapshot.SnapshotId,
                        incomingCaller.SymbolId,
                        selected.SymbolId,
                        null,
                        "Calls",
                        "fixture:incoming"),
                    new IndexRelationshipRecord(
                        "older-outgoing-call",
                        olderSnapshot.SnapshotId,
                        selected.SymbolId,
                        outgoingCallee.SymbolId,
                        null,
                        "Calls",
                        "fixture:outgoing")
                ]),
            "2026-08-14T04:01:00Z",
            cancellationToken);

        var newerSnapshot = new CodeSnapshotRecord(
            "snapshot-newer",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-newer",
            "2026-08-15T04:00:00Z");
        await _repository.CreateCodeSnapshotAsync(newerSnapshot, cancellationToken);
        var newerRun = new IndexRunRecord(
            "index-newer",
            newerSnapshot.SnapshotId,
            IndexRunStatus.Running,
            newerSnapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(newerRun, cancellationToken);
        await _repository.CompleteIndexRunAsync(
            newerRun.IndexId,
            new IndexWriteSet(
                [
                    new IndexSymbolRecord(
                        "newer-beta",
                        newerSnapshot.SnapshotId,
                        "ScheduleI:Installed:Type:Beta.Widget",
                        "Type",
                        "Beta.Widget",
                        "Beta.Widget",
                        false)
                ],
                [],
                [],
                [],
                []),
            "2026-08-15T04:01:00Z",
            cancellationToken);

        return new ScopedIndexFixture(
            olderRun,
            dataRoot,
            selected,
            sourceFile,
            selectedPath,
            location);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record ScopedIndexFixture(
        IndexRunRecord OlderRun,
        string DataRoot,
        IndexSymbolRecord Selected,
        IndexSourceFileRecord SourceFile,
        string SourcePath,
        IndexSourceLocationRecord Location);
}
