using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Storage.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class DiffCliTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-diff-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Diff_json_filters_changes_bounds_results_and_labels_both_snapshot_channels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var application = new CliApplication(_root, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["diff", "--codebase", "s1api", "--from", "index-diff-from", "--to", "index-diff-to", "--kind", "BodyChanged", "--limit", "1", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        var data = root.GetProperty("data");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, data.GetProperty("returnedCount").GetInt32());
        Assert.Equal("index-diff-from", data.GetProperty("from").GetProperty("indexId").GetString());
        Assert.Equal("index-diff-to", data.GetProperty("to").GetProperty("indexId").GetString());
        Assert.Equal("Installed", data.GetProperty("from").GetProperty("channel").GetString());
        Assert.Equal("Release", data.GetProperty("to").GetProperty("channel").GetString());
        Assert.Equal("BodyChanged", data.GetProperty("changes")[0].GetProperty("kinds")[0].GetString());
    }

    [Fact]
    public async Task Diff_human_output_includes_counts_exact_ids_and_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var application = new CliApplication(_root, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["diff", "--codebase", "s1api", "--from", "index-diff-from", "--to", "index-diff-to"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Found 2 changes. Showing 2.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("method-Installed -> method-Release", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("method-body", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_all_includes_unchanged_and_standalone_body_unavailable_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var application = new CliApplication(_root, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["diff", "--codebase", "s1api", "--from", "index-diff-from", "--to", "index-diff-to", "--all", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(0, exitCode);
        Assert.Equal(4, data.GetProperty("totalCount").GetInt32());
        Assert.Contains(data.GetProperty("changes").EnumerateArray(), change => change.GetProperty("kinds").EnumerateArray().Any(kind => kind.GetString() == "Unchanged"));
        Assert.Contains(data.GetProperty("changes").EnumerateArray(), change => change.GetProperty("kinds").EnumerateArray().Any(kind => kind.GetString() == "BodyUnavailable"));
    }

    [Fact]
    public async Task Diff_missing_channel_returns_no_completed_index_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var application = new CliApplication(_root, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["diff", "--codebase", "s1api", "--from", "preview", "--to", "index-diff-to", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal("NoCompletedIndex", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var repository = new SqliteAtlasRepository(new AtlasPaths(_root).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        await SeedIndexAsync(repository, CodeChannel.Installed, "snapshot-diff-from", "index-diff-from", cancellationToken, bodyFingerprint: "old-body", includeAdded: false);
        await SeedIndexAsync(repository, CodeChannel.Release, "snapshot-diff-to", "index-diff-to", cancellationToken, bodyFingerprint: "new-body", includeAdded: true);
    }

    private static async Task SeedIndexAsync(
        SqliteAtlasRepository repository,
        CodeChannel channel,
        string snapshotId,
        string indexId,
        CancellationToken cancellationToken,
        string bodyFingerprint,
        bool includeAdded)
    {
        var snapshot = new CodeSnapshotRecord(snapshotId, CodebaseKind.S1Api, channel, channel.ToString(), "2026-08-14T00:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        var symbols = new List<IndexSymbolRecord>
        {
            new("type-" + channel, snapshotId, $"S1Api:{channel}:Type:Demo.Widget", "Type", "Demo.Widget", "Demo.Widget", false),
            new("method-" + channel, snapshotId, $"S1Api:{channel}:Method:Demo.Widget::Run()", "Method", "Demo.Widget.Run", "System.Void Demo.Widget::Run()", false, BodyRecoveryStatus.Recovered),
            new("stub-" + channel, snapshotId, $"S1Api:{channel}:Method:Demo.Widget::Stub()", "Method", "Demo.Widget.Stub", "System.Void Demo.Widget::Stub()", false, BodyRecoveryStatus.StubOrUnavailable)
        };
        if (includeAdded)
            symbols.Add(new("added-" + channel, snapshotId, $"S1Api:{channel}:Type:Demo.Added", "Type", "Demo.Added", "Demo.Added", false));
        var fingerprints = new List<IndexFingerprintRecord>
        {
            new("type-" + channel, "declaration", "type-declaration"),
            new("type-" + channel, "structural", "type-structural"),
            new("method-" + channel, "declaration", "method-declaration"),
            new("method-" + channel, "structural", "method-structural"),
            new("method-" + channel, "method-body", bodyFingerprint),
            new("stub-" + channel, "declaration", "stub-declaration"),
            new("stub-" + channel, "structural", "stub-structural")
        };
        if (includeAdded)
        {
            fingerprints.Add(new IndexFingerprintRecord("added-" + channel, "declaration", "added-declaration"));
            fingerprints.Add(new IndexFingerprintRecord("added-" + channel, "structural", "added-structural"));
        }
        await repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, [], [], fingerprints, []), "2026-08-14T00:01:00Z", cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
