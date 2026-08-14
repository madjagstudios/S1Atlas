using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class IndexQueryServiceUsabilityTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-query-usability-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly SqliteAtlasRepository _repository;

    public IndexQueryServiceUsabilityTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Search_returns_exact_total_and_bounded_returned_count()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Release,
            [
                Symbol("release-a", CodeChannel.Release, "DealerAlpha"),
                Symbol("release-b", CodeChannel.Release, "DealerBeta"),
                Symbol("release-c", CodeChannel.Release, "DealerGamma")
            ],
            cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "dealer",
            new IndexQueryOptions(CodebaseKind.S1Api, CodeChannel.Release, Limit: 2),
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task All_channels_are_merged_in_global_rank_order_before_one_total_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Release,
            [Symbol("release-substring", CodeChannel.Release, "Demo.SuperDealerArchive")],
            cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Preview,
            [Symbol("preview-exact", CodeChannel.Preview, "Dealer")],
            cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "dealer",
            new IndexQueryOptions(
                CodebaseKind.S1Api,
                Channel: CodeChannel.Release,
                AllChannels: true,
                Limit: 1),
            cancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);
        var best = Assert.Single(result.Results);
        Assert.Equal("Preview", best.Channel);
        Assert.Equal("preview-exact", best.SymbolId);
    }

    [Fact]
    public async Task Search_rejects_nonpositive_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(
                "dealer",
                new IndexQueryOptions(CodebaseKind.S1Api, CodeChannel.Release, Limit: 0),
                cancellationToken));
    }

    [Fact]
    public async Task Source_resolves_one_symbol_and_returns_hash_verified_exact_snippet_with_body_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedScheduleOneSourceAsync(BodyRecoveryStatus.Recovered, cancellationToken);
        var service = new IndexQueryService(_repository, _dataRoot);

        var result = await service.SourceAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        var snippet = Assert.IsType<SourceSnippetQueryResult>(result.Snippet);
        Assert.Equal(fixture.Selected.SymbolId, snippet.Symbol.SymbolId);
        Assert.Equal(fixture.Selected.Signature, snippet.Symbol.Signature);
        Assert.Equal(fixture.IndexId, snippet.IndexId);
        Assert.Equal(fixture.SourceFile.RelativePath, snippet.RelativePath);
        Assert.Equal(fixture.SourceFile.Sha256, snippet.Sha256);
        Assert.Equal(fixture.SourceFile.ByteCount, snippet.ByteCount);
        Assert.Equal(fixture.Location.StartLine, snippet.Location.StartLine);
        Assert.Equal(fixture.Location.StartColumn, snippet.Location.StartColumn);
        Assert.Equal(fixture.Location.EndLine, snippet.Location.EndLine);
        Assert.Equal(fixture.Location.EndColumn, snippet.Location.EndColumn);
        Assert.Equal("public void Run() { }", snippet.Text);
        Assert.Equal(0, snippet.ContextBefore);
        Assert.Equal(0, snippet.ContextAfter);
        Assert.Equal(BodyRecoveryStatus.Recovered, snippet.BodyRecoveryStatus);
        Assert.Equal("ScheduleI:Installed:generated", snippet.Provenance);
    }

    [Fact]
    public async Task Source_returns_structured_ambiguity_and_does_not_choose_a_candidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedScheduleOneSourceAsync(BodyRecoveryStatus.Recovered, cancellationToken);
        var service = new IndexQueryService(_repository, _dataRoot);

        var result = await service.SourceAsync(
            "dealer",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            5,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Null(result.Snippet);
        Assert.Equal(2, result.Resolution.Candidates.Count);
    }

    [Fact]
    public async Task Source_rejects_a_hash_mismatch_in_the_selected_source_but_ignores_unrelated_source_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedScheduleOneSourceAsync(BodyRecoveryStatus.Recovered, cancellationToken);
        var service = new IndexQueryService(_repository, _dataRoot);

        var unrelatedPath = Path.Combine(fixture.IndexRoot, fixture.UnrelatedSourceFile.RelativePath);
        await File.WriteAllTextAsync(unrelatedPath, "tampered unrelated bytes", cancellationToken);

        var first = await service.SourceAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken);
        Assert.NotNull(first.Snippet);

        await File.AppendAllTextAsync(fixture.SelectedPath, "tampered", cancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SourceAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken));
    }

    [Theory]
    [InlineData(BodyRecoveryStatus.NoBodyByDesign)]
    [InlineData(BodyRecoveryStatus.Recovered)]
    [InlineData(BodyRecoveryStatus.StubOrUnavailable)]
    [InlineData(BodyRecoveryStatus.Unknown)]
    public async Task Source_preserves_every_persisted_callable_body_status(BodyRecoveryStatus status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedScheduleOneSourceAsync(status, cancellationToken);
        var service = new IndexQueryService(_repository, _dataRoot);

        var result = await service.SourceAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken);

        Assert.Equal(status, result.Snippet?.BodyRecoveryStatus);
    }

    [Fact]
    public async Task Source_maps_legacy_callable_null_to_unknown_but_leaves_noncallable_null()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedScheduleOneSourceAsync(null, cancellationToken);
        var service = new IndexQueryService(_repository, _dataRoot);

        var method = await service.SourceAsync(
            fixture.Selected.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken);
        var type = await service.SourceAsync(
            fixture.Type.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed),
            0,
            cancellationToken);

        Assert.Equal(BodyRecoveryStatus.Unknown, method.Snippet?.BodyRecoveryStatus);
        Assert.Null(type.Snippet?.BodyRecoveryStatus);
    }

    private async Task SeedChannelAsync(
        CodeChannel channel,
        IReadOnlyList<IndexSymbolRecord> symbols,
        CancellationToken cancellationToken)
    {
        var snapshotId = "snapshot-" + channel;
        var indexId = "index-" + channel;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            channel,
            "source-" + channel,
            "2026-08-14T04:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T04:01:00Z",
            cancellationToken);
    }

    private async Task<SourceFixture> SeedScheduleOneSourceAsync(
        BodyRecoveryStatus? bodyRecoveryStatus,
        CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var indexId = new string('a', 64);
        var buildId = new string('b', 64);
        var snapshotId = "snapshot-source";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-source",
            "2026-08-14T05:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var type = new IndexSymbolRecord(
            "type-source",
            snapshotId,
            "ScheduleI:Installed:Type:Demo.Widget",
            "Type",
            "Demo.Widget",
            "Demo.Widget",
            false,
            null);
        var selected = new IndexSymbolRecord(
            "method-source",
            snapshotId,
            "ScheduleI:Installed:Method:Demo.Widget::Run()",
            "Method",
            "Demo.Widget.Run",
            "System.Void Demo.Widget::Run()",
            false,
            bodyRecoveryStatus);
        var dealerA = new IndexSymbolRecord(
            "dealer-a",
            snapshotId,
            "ScheduleI:Installed:Type:Alpha.DealerService",
            "Type",
            "Alpha.DealerService",
            "Alpha.DealerService",
            false);
        var dealerB = new IndexSymbolRecord(
            "dealer-b",
            snapshotId,
            "ScheduleI:Installed:Type:Beta.DealerService",
            "Type",
            "Beta.DealerService",
            "Beta.DealerService",
            false);

        const string sourceText = "namespace Demo;\npublic class Widget\n{\n    public void Run() { }\n}\n";
        const string unrelatedText = "this file must never be read for Widget.Run";
        var sourceFile = new IndexSourceFileRecord(
            "file-source",
            snapshotId,
            "Assembly-CSharp.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var unrelatedSourceFile = new IndexSourceFileRecord(
            "file-unrelated",
            snapshotId,
            "Unrelated.cs",
            Sha256(unrelatedText),
            Encoding.UTF8.GetByteCount(unrelatedText));
        var methodLocation = new IndexSourceLocationRecord(
            selected.SymbolId,
            sourceFile.SourceFileId,
            4,
            5,
            4,
            26);
        var typeLocation = new IndexSourceLocationRecord(
            type.SymbolId,
            sourceFile.SourceFileId,
            2,
            1,
            5,
            2);

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [type, selected, dealerA, dealerB],
                [sourceFile, unrelatedSourceFile],
                [methodLocation, typeLocation],
                [],
                []),
            "2026-08-14T05:01:00Z",
            cancellationToken);

        var indexRoot = Path.Combine(_dataRoot, "builds", buildId, "indexes", indexId);
        Directory.CreateDirectory(indexRoot);
        var selectedPath = Path.Combine(indexRoot, sourceFile.RelativePath);
        await File.WriteAllTextAsync(selectedPath, sourceText, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, unrelatedSourceFile.RelativePath),
            unrelatedText,
            new UTF8Encoding(false),
            cancellationToken);

        return new SourceFixture(
            indexId,
            indexRoot,
            selectedPath,
            selected,
            type,
            sourceFile,
            unrelatedSourceFile,
            methodLocation);
    }

    private static IndexSymbolRecord Symbol(string id, CodeChannel channel, string qualifiedName) =>
        new(
            id,
            "snapshot-" + channel,
            "S1Api:" + channel + ":Type:" + qualifiedName,
            "Type",
            qualifiedName,
            qualifiedName,
            false);

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record SourceFixture(
        string IndexId,
        string IndexRoot,
        string SelectedPath,
        IndexSymbolRecord Selected,
        IndexSymbolRecord Type,
        IndexSourceFileRecord SourceFile,
        IndexSourceFileRecord UnrelatedSourceFile,
        IndexSourceLocationRecord Location);
}
