using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Diff;

public sealed class DiffCommandTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-cli-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public DiffCommandTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Diff_json_reports_added_and_removed_symbols()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('0', 63);
        var buildIdB = "b" + new string('0', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a", "idx-a",
            [MakeSymbol("ScheduleI:Installed:Method:Old::Run():System.Void", "Method", "Old.Run", "Old::Run():System.Void")],
            [MakeFingerprint("sym-0", "declaration", "aaa")],
            [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b", "idx-b",
            [MakeSymbol("ScheduleI:Installed:Method:New::Start():System.Void", "Method", "New.Start", "New::Start():System.Void")],
            [MakeFingerprint("sym-0", "declaration", "bbb")],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("counts").GetProperty("removed").GetInt32());
        Assert.Equal(2, data.GetProperty("totalChanged").GetInt32());
    }

    [Fact]
    public async Task Diff_human_output_contains_summary_and_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('1', 63);
        var buildIdB = "b" + new string('1', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a1", "idx-a1",
            [MakeSymbol("ScheduleI:Installed:Type:Ns.Stable", "Type", "Ns.Stable", "Ns.Stable")],
            [MakeFingerprint("sym-0", "declaration", "same")],
            [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b1", "idx-b1",
            [MakeSymbol("ScheduleI:Installed:Type:Ns.Stable", "Type", "Ns.Stable", "Ns.Stable")],
            [MakeFingerprint("sym-0", "declaration", "same")],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB], output, error, ct);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Build diff:", text, StringComparison.Ordinal);
        Assert.Contains("Unchanged:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_unknown_build_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var fakeA = "f" + new string('0', 63);
        var fakeB = "f" + new string('1', 63);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", fakeA, fakeB, "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        var text = output.ToString();
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diff_unsupported_channel_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", "a", "b", "--channel", "release", "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("UnsupportedChannel", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_same_index_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildId = "c" + new string('0', 63);
        await SeedScheduleIBuildWithIndexAsync(buildId, "ext-same", "idx-same", [], [], [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildId, buildId, "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("SameIndex", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_limit_truncates_changes_but_counts_remain_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('2', 63);
        var buildIdB = "b" + new string('2', 63);
        var symbols = Enumerable.Range(0, 5)
            .Select(i => MakeSymbol($"ScheduleI:Installed:Method:Ns::M{i}():System.Void", "Method", $"Ns.M{i}", $"Ns::M{i}():System.Void"))
            .ToArray();
        var fps = symbols.Select((_, i) => MakeFingerprint($"sym-{i}", "declaration", $"hash-{i}")).ToArray();

        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a2", "idx-a2", [], [], [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b2", "idx-b2", symbols, fps, [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--limit", "2", "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(5, data.GetProperty("totalChanged").GetInt32());
        Assert.Equal(2, data.GetProperty("returnedCount").GetInt32());
        Assert.Equal(2, data.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Diff_kind_filter_restricts_counts_and_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('3', 63);
        var buildIdB = "b" + new string('3', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a3", "idx-a3", [], [], [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b3", "idx-b3",
            [
                MakeSymbol("ScheduleI:Installed:Method:Ns::Do():System.Void", "Method", "Ns.Do", "Ns::Do():System.Void"),
                MakeSymbol("ScheduleI:Installed:Type:Ns.MyType", "Type", "Ns.MyType", "Ns.MyType")
            ],
            [
                MakeFingerprint("sym-0", "declaration", "m-hash"),
                MakeFingerprint("sym-1", "declaration", "t-hash")
            ],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--kind", "Method", "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("totalChanged").GetInt32());
    }

    // --- Helpers ---

    private IndexSymbolRecord MakeSymbol(string canonicalKey, string kind, string qualifiedName, string signature) =>
        new("placeholder", "placeholder", canonicalKey, kind, qualifiedName, signature, false);

    private IndexFingerprintRecord MakeFingerprint(string symbolId, string kind, string hash) =>
        new(symbolId, kind, hash);

    private async Task SeedScheduleIBuildWithIndexAsync(
        string buildId, string extractionId, string indexId,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyList<IndexFingerprintRecord> fingerprints,
        IReadOnlyList<IndexRelationshipRecord> relationships,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var build = await _repository.GetBuildAsync(buildId, ct);
        if (build is null)
        {
            var envSnapshot = new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(buildId, "asm-hash", "meta-hash", now, IsValid: true),
                Installation: InstallationObservation.Unknown,
                Dependencies: [],
                AtlasVersion: "0.1.0-test",
                CapturedAtUtc: now);
            await ((IAtlasRepository)_repository).SaveSnapshotAsync(envSnapshot, ct);
        }

        var pref = await _repository.GetPreferredExtractionAsync(buildId, ct);
        if (pref is null)
        {
            await SeedPreferredExtractionDirectAsync(buildId, extractionId, now);
        }

        var snapshotId = "snap-" + indexId;
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, now.ToString("O")), ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, now.ToString("O")), ct);

        var realSymbols = symbols.Select((s, i) => s with { SymbolId = $"{indexId}-sym-{i}", SnapshotId = snapshotId }).ToArray();
        var realFps = fingerprints.Select(fp => fp with { SymbolId = $"{indexId}-{fp.SymbolId}" }).ToArray();
        var realRels = relationships.Select(r => r with { SnapshotId = snapshotId }).ToArray();

        await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(realSymbols, [], [], realFps, realRels), now.ToString("O"), ct);
    }

    private async Task SeedPreferredExtractionDirectAsync(string buildId, string extractionId, DateTimeOffset now)
    {
        var dbPath = Path.Combine(_dataDirectory, "atlas.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = OFF;";
        await pragma.ExecuteNonQueryAsync();

        await using var insertExtraction = connection.CreateCommand();
        insertExtraction.CommandText = """
            INSERT OR IGNORE INTO validated_extractions
                (extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id,
                 profile_id, profile_version, profile_digest, adapter_version, extraction_schema_version,
                 artifact_manifest_digest, root_path, created_at_utc, trust_level, validation_outcome,
                 artifact_count, library_count, managed_assembly_count, type_count, method_count,
                 field_count, property_count, event_count, total_output_bytes, total_managed_bytes)
            VALUES
                ($extractionId, 'recipe-stub', $buildId, 'tool-stub', 'attempt-stub',
                 'profile-stub', 1, 'digest-stub', 1, 1,
                 'manifest-stub', 'C:\stub', $now, 'Full', 'Valid',
                 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            """;
        insertExtraction.Parameters.AddWithValue("$extractionId", extractionId);
        insertExtraction.Parameters.AddWithValue("$buildId", buildId);
        insertExtraction.Parameters.AddWithValue("$now", now.ToString("O"));
        await insertExtraction.ExecuteNonQueryAsync();

        await using var insertPref = connection.CreateCommand();
        insertPref.CommandText = """
            INSERT OR IGNORE INTO preferred_extractions (build_id, extraction_id, selected_at_utc, selection_reason)
            VALUES ($buildId, $extractionId, $now, 'ManagedAutomatic');
            """;
        insertPref.Parameters.AddWithValue("$buildId", buildId);
        insertPref.Parameters.AddWithValue("$extractionId", extractionId);
        insertPref.Parameters.AddWithValue("$now", now.ToString("O"));
        await insertPref.ExecuteNonQueryAsync();
    }
}
