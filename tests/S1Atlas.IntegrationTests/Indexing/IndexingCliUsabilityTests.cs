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

    [Fact]
    public async Task Refs_human_output_contains_both_directions_enriched_ids_evidence_and_unresolved_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "target"],
            output,
            error,
            cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("edge-in", text, StringComparison.Ordinal);
        Assert.Contains("Calls", text, StringComparison.Ordinal);
        Assert.Contains("call-site", text, StringComparison.Ordinal);
        Assert.Contains("Demo.Caller.Run", text, StringComparison.Ordinal);
        Assert.Contains("caller", text, StringComparison.Ordinal);
        Assert.Contains("Demo.Target.Run", text, StringComparison.Ordinal);
        Assert.Contains("target", text, StringComparison.Ordinal);
        Assert.Contains("edge-unresolved", text, StringComparison.Ordinal);
        Assert.Contains("External.Api::Ping()", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callers_and_callees_have_distinct_call_like_semantics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");

        using var callersOutput = new StringWriter();
        using var callersError = new StringWriter();
        var callersExit = application.Invoke(
            ["callers", "target"],
            callersOutput,
            callersError,
            cancellationToken);

        using var calleesOutput = new StringWriter();
        using var calleesError = new StringWriter();
        var calleesExit = application.Invoke(
            ["callees", "target"],
            calleesOutput,
            calleesError,
            cancellationToken);

        Assert.Equal(0, callersExit);
        Assert.Equal(0, calleesExit);
        Assert.Equal(string.Empty, callersError.ToString());
        Assert.Equal(string.Empty, calleesError.ToString());
        Assert.Contains("edge-in", callersOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("edge-out", callersOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("edge-out", calleesOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("edge-unresolved", calleesOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("edge-in", calleesOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relationship_ambiguity_is_nonzero_and_returns_structured_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "service", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("AmbiguousSymbol", root.GetProperty("error").GetProperty("code").GetString());
        var candidates = root.GetProperty("data").GetProperty("candidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.Contains(candidates.EnumerateArray(), item => item.GetProperty("symbolId").GetString() == "service-a");
        Assert.Contains(candidates.EnumerateArray(), item => item.GetProperty("symbolId").GetString() == "service-b");
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

    private async Task SeedRelationshipIndexAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-relationships";
        const string indexId = "index-cli-relationships";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-relationships",
            "2026-08-14T18:22:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            new IndexSymbolRecord("target", snapshotId, "ScheduleI:Installed:Method:Demo.Target::Run()", "Method", "Demo.Target.Run", "System.Void Demo.Target::Run()", false, BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord("caller", snapshotId, "ScheduleI:Installed:Method:Demo.Caller::Run()", "Method", "Demo.Caller.Run", "System.Void Demo.Caller::Run()", false, BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord("callee", snapshotId, "ScheduleI:Installed:Constructor:Demo.Callee::.ctor()", "Constructor", "Demo.Callee..ctor", "System.Void Demo.Callee::.ctor()", false, BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord("service-a", snapshotId, "ScheduleI:Installed:Type:Alpha.Service", "Type", "Alpha.Service", "Alpha.Service", false),
            new IndexSymbolRecord("service-b", snapshotId, "ScheduleI:Installed:Type:Beta.Service", "Type", "Beta.Service", "Beta.Service", false)
        };
        var relationships = new[]
        {
            new IndexRelationshipRecord("edge-in", snapshotId, "caller", "target", null, "Calls", "call-site"),
            new IndexRelationshipRecord("edge-out", snapshotId, "target", "callee", null, "Constructs", "new-site"),
            new IndexRelationshipRecord("edge-unresolved", snapshotId, "target", null, "External.Api::Ping()", "Calls", "unresolved-call")
        };
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], relationships),
            "2026-08-14T18:23:00Z",
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
