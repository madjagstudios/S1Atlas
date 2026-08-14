using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class IndexingCliUsabilityTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-cli-usability-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;

    public IndexingCliUsabilityTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
    }

    [Fact]
    public async Task Search_json_reports_exact_total_returned_count_and_honors_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSearchIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["search", "dealer", "--limit", "3", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(60, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, data.GetProperty("returnedCount").GetInt32());
        Assert.Equal(3, data.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Search_human_output_includes_readable_name_and_exact_symbol_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSearchIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["search", "Dealer000", "--limit", "1"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Demo.Dealer000", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("dealer-000", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Search_rejects_nonpositive_limit_with_stable_code(string value)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSearchIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["search", "dealer", "--limit", value, "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("InvalidLimit", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task SeedSearchIndexAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-search";
        const string indexId = "index-cli-search";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-search",
            "2026-08-14T18:20:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = Enumerable.Range(0, 60)
            .Select(index =>
            {
                var suffix = index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                return new IndexSymbolRecord(
                    "dealer-" + suffix,
                    snapshotId,
                    "ScheduleI:Installed:Type:Demo.Dealer" + suffix,
                    "Type",
                    "Demo.Dealer" + suffix,
                    "Demo.Dealer" + suffix,
                    false);
            })
            .ToArray();
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T18:21:00Z",
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
