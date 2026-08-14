using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
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
        Assert.Contains("Found 1 matches. Showing 1.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Relationship_query_reports_no_completed_index_distinctly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["callers", "MissingType", "--codebase", "s1mapi", "--channel", "installed", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("NoCompletedIndex", document.RootElement.GetProperty("error").GetProperty("code").GetString());
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
    public async Task Source_human_output_contains_focused_metadata_and_defaults_to_five_lines_context()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = CreateSmallSource();
        var seeded = await SeedSourceIndexAsync(source, cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["source", "source-target"], output, error, cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("source-target", text, StringComparison.Ordinal);
        Assert.Contains("System.Void Demo.SourceTarget::Run()", text, StringComparison.Ordinal);
        Assert.Contains("generated/Demo.SourceTarget.cs", text, StringComparison.Ordinal);
        Assert.Contains(seeded.Sha256, text, StringComparison.Ordinal);
        Assert.Contains("6:1-6:22", text, StringComparison.Ordinal);
        Assert.Contains("Recovered", text, StringComparison.Ordinal);
        Assert.Contains("line-1", text, StringComparison.Ordinal);
        Assert.Contains("line-11", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_context_zero_returns_exact_recorded_span_and_negative_context_is_stable_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateSmallSource(), cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");

        using var exactOutput = new StringWriter();
        using var exactError = new StringWriter();
        var exactExit = application.Invoke(
            ["source", "source-target", "--context", "0"],
            exactOutput,
            exactError,
            cancellationToken);

        Assert.Equal(0, exactExit);
        Assert.Equal(string.Empty, exactError.ToString());
        Assert.Contains("public void Run() { }", exactOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("line-5", exactOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("line-7", exactOutput.ToString(), StringComparison.Ordinal);

        using var invalidOutput = new StringWriter();
        using var invalidError = new StringWriter();
        var invalidExit = application.Invoke(
            ["source", "source-target", "--context", "-1", "--json"],
            invalidOutput,
            invalidError,
            cancellationToken);

        Assert.Equal(1, invalidExit);
        Assert.Equal(string.Empty, invalidError.ToString());
        using var document = JsonDocument.Parse(invalidOutput.ToString());
        Assert.Equal("InvalidContext", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Source_file_refuses_more_than_one_mib_on_stdout_without_output()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var largeSource = "public void Run() { }\n" + new string('x', 1_048_576);
        await SeedSourceIndexAsync(largeSource, cancellationToken, startLine: 1, endColumn: 22);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--file", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("SourceTooLargeForTerminal", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Source_output_writes_hash_verified_full_file_outside_game_root()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = CreateSmallSource();
        await SeedSourceIndexAsync(source, cancellationToken);
        var destination = Path.Combine(_root, "exports", "Demo.SourceTarget.cs");
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--file", "--output", destination],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.True(File.Exists(destination));
        Assert.Equal(source, await File.ReadAllTextAsync(destination, cancellationToken));
    }

    [Fact]
    public async Task Source_output_under_detected_game_root_is_rejected_without_creating_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gameRoot = Path.Combine(_root, "Schedule I");
        Directory.CreateDirectory(gameRoot);
        await SeedSourceIndexAsync(CreateSmallSource(), cancellationToken, installationRoot: gameRoot);
        var destination = Path.Combine(gameRoot, "Exports", "Demo.SourceTarget.cs");
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--file", "--output", destination, "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.False(File.Exists(destination));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("ReadOnlyGameInstallation", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Source_selects_cached_release_and_preview_api_indexes_by_explicit_channel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var commitSha = new string('f', 40);
        await SeedCachedApiSourceIndexAsync(
            CodeChannel.Release,
            commitSha,
            new string('1', 64),
            "cached-source-target-release",
            "src/Demo/CachedSourceTarget.cs",
            """
            namespace Demo;

            public sealed class CachedSourceTarget
            {
                public void Run()
                {
                    ReleaseOnly();
                }
            }
            """,
            cancellationToken);
        await SeedCachedApiSourceIndexAsync(
            CodeChannel.Preview,
            commitSha,
            new string('2', 64),
            "cached-source-target-preview",
            "src/Demo/CachedSourceTarget.cs",
            """
            namespace Demo;

            public sealed class CachedSourceTarget
            {
                public void Run()
                {
                    PreviewOnly();
                }
            }
            """,
            cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");

        using var releaseOutput = new StringWriter();
        using var releaseError = new StringWriter();
        var releaseExit = application.Invoke(
            ["source", "Demo.CachedSourceTarget.Run", "--codebase", "s1api", "--channel", "release"],
            releaseOutput,
            releaseError,
            cancellationToken);

        using var previewOutput = new StringWriter();
        using var previewError = new StringWriter();
        var previewExit = application.Invoke(
            ["source", "Demo.CachedSourceTarget.Run", "--codebase", "s1api", "--channel", "preview"],
            previewOutput,
            previewError,
            cancellationToken);

        Assert.Equal(0, releaseExit);
        Assert.Equal(0, previewExit);
        Assert.Equal(string.Empty, releaseError.ToString());
        Assert.Equal(string.Empty, previewError.ToString());
        Assert.Contains("ReleaseOnly();", releaseOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewOnly();", releaseOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("PreviewOnly();", previewOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseOnly();", previewOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_rejects_schedule_i_release_channel_with_stable_error()
    {
        await SaveCurrentSnapshotAsync(TestContext.Current.CancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "schedule-i", "--channel", "release", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("InvalidCodebaseChannel", document.RootElement.GetProperty("error").GetProperty("code").GetString());
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

    private async Task<SeededSource> SeedSourceIndexAsync(
        string source,
        CancellationToken cancellationToken,
        int startLine = 6,
        int endColumn = 22,
        string? installationRoot = null)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-source";
        const string indexId = "index-cli-source";
        const string relativePath = "generated/Demo.SourceTarget.cs";
        var bytes = Encoding.UTF8.GetBytes(source);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var indexRoot = Path.Combine(_dataRoot, "builds", "build-source", "indexes", indexId);
        var sourcePath = Path.Combine(indexRoot, "generated", "Demo.SourceTarget.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken);

        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-source",
            "2026-08-14T18:24:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [new IndexSymbolRecord(
                    "source-target",
                    snapshotId,
                    "ScheduleI:Installed:Method:Demo.SourceTarget::Run()",
                    "Method",
                    "Demo.SourceTarget.Run",
                    "System.Void Demo.SourceTarget::Run()",
                    false,
                    BodyRecoveryStatus.Recovered)],
                [new IndexSourceFileRecord("source-file", snapshotId, relativePath, sha256, bytes.LongLength)],
                [new IndexSourceLocationRecord("source-target", "source-file", startLine, 1, startLine, endColumn)],
                [],
                []),
            "2026-08-14T18:25:00Z",
            cancellationToken);

        if (installationRoot is not null)
        {
            var timestamp = DateTimeOffset.Parse("2026-08-14T18:26:00Z");
            var fullRoot = Path.GetFullPath(installationRoot);
            await repository.SaveSnapshotAsync(
                new EnvironmentSnapshot(
                    2,
                    new GameBuild("build-source", "assembly-hash", "metadata-hash", timestamp, true),
                    new InstallationObservation(
                        "2022.3.62",
                        "3164500",
                        "test-build",
                        fullRoot,
                        Path.Combine(fullRoot, "GameAssembly.dll"),
                        Path.Combine(fullRoot, "Schedule I_Data", "il2cpp_data", "Metadata", "global-metadata.dat")),
                    [],
                    "0.1.0-test",
                    timestamp),
                cancellationToken);
        }

        return new SeededSource(sha256);
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

    private async Task SeedCachedApiSourceIndexAsync(
        CodeChannel channel,
        string commitSha,
        string indexId,
        string symbolId,
        string relativePath,
        string source,
        CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var snapshotId = "snapshot-cli-cached-source-" + channel.ToString().ToLowerInvariant();
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalizedSource);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var indexRoot = Path.Combine(_dataRoot, "upstream", "s1api", "commits", commitSha, "indexes", indexId);
        var sourcePath = Path.Combine(indexRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken);

        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.S1Api,
                channel,
                commitSha,
                "2026-08-14T18:30:00Z"),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, "2026-08-14T18:31:00Z"),
            cancellationToken);
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [new IndexSymbolRecord(
                    symbolId,
                    snapshotId,
                    "S1Api:" + channel + ":Method:Demo.CachedSourceTarget::Run()",
                    "Method",
                    "Demo.CachedSourceTarget.Run",
                    "System.Void Demo.CachedSourceTarget::Run()",
                    false,
                    BodyRecoveryStatus.Recovered)],
                [new IndexSourceFileRecord("cached-source-file-" + channel.ToString().ToLowerInvariant(), snapshotId, relativePath, sha256, bytes.LongLength)],
                [new IndexSourceLocationRecord(symbolId, "cached-source-file-" + channel.ToString().ToLowerInvariant(), 1, 1, 9, 2)],
                [],
                []),
            "2026-08-14T18:32:00Z",
            cancellationToken);
    }

    private async Task SaveCurrentSnapshotAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(
                    new string('a', 64),
                    new string('b', 64),
                    new string('c', 64),
                    DateTimeOffset.Parse("2026-08-14T18:29:00Z"),
                    true),
                new InstallationObservation("2022.3.62", "3164500", "fixture", _root, null, null),
                [],
                "0.1.0-test",
                DateTimeOffset.Parse("2026-08-14T18:29:00Z")),
            cancellationToken);
    }

    private static string CreateSmallSource() => string.Join(
        '\n',
        "line-1",
        "line-2",
        "line-3",
        "line-4",
        "line-5",
        "public void Run() { }",
        "line-7",
        "line-8",
        "line-9",
        "line-10",
        "line-11",
        "line-12");

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record SeededSource(string Sha256);
}
