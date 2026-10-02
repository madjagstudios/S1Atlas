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
using S1Atlas.TestSupport;
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
            ["search", "dealer", "--channel", "all", "--limit", "3", "--json"],
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
            ["search", "Dealer000", "--channel", "all", "--limit", "1"],
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
            ["search", "dealer", "--channel", "all", "--limit", value, "--json"],
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

        var exitCode = application.Invoke(["source", "source-target", "--channel", "all"], output, error, cancellationToken);

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
    public async Task Source_json_includes_runtime_verification_neighborhood_and_notice_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = CreateRuntimeSource();
        await SeedSourceIndexAsync(source, cancellationToken, endColumn: 53);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        var verification = data.GetProperty("runtimeVerification");
        Assert.Contains(0, verification.GetProperty("signals").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Contains("in-game", verification.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Object, data.GetProperty("neighborhood").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("neighborhoodNotice").ValueKind);
    }

    [Fact]
    public async Task Source_human_output_reports_runtime_guidance_and_neighborhood_summary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateRuntimeSource(), cancellationToken, endColumn: 53);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all"],
            output,
            error,
            cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Runtime verification:", text, StringComparison.Ordinal);
        Assert.Contains("in-game", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Neighborhood:", text, StringComparison.Ordinal);
        Assert.Contains("Callers: 0/0", text, StringComparison.Ordinal);
        Assert.Contains("Callees: 0/0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_related_limit_zero_omits_neighborhood()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateRuntimeSource(), cancellationToken, endColumn: 53);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all", "--related-limit", "0", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("neighborhood").ValueKind);
    }

    [Fact]
    public async Task Source_related_limit_accepts_the_upper_bound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateRuntimeSource(), cancellationToken, endColumn: 53);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all", "--related-limit", "50", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Source_full_type_returns_containing_type_span_without_neighborhood()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = string.Join(
            '\n',
            "namespace Demo;",
            "public sealed class SourceTarget",
            "{",
            "    public void Run() { }",
            "}");
        await SeedSourceIndexAsync(
            source,
            cancellationToken,
            startLine: 4,
            endColumn: 26,
            includeContainingType: true,
            containingTypeStartLine: 2,
            containingTypeEndLine: 5);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all", "--full-type", "--context", "0", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Type", data.GetProperty("symbol").GetProperty("kind").GetString());
        Assert.Contains("public sealed class SourceTarget", data.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("neighborhood").ValueKind);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("51")]
    public async Task Source_rejects_related_limit_outside_zero_to_fifty(string value)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateSmallSource(), cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "source-target", "--channel", "all", "--related-limit", value, "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("InvalidRelatedLimit", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("--file")]
    [InlineData("--output")]
    public async Task Source_full_type_rejects_complete_file_modes(string conflictingOption)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSourceIndexAsync(CreateSmallSource(), cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] arguments = conflictingOption == "--output"
            ? ["source", "source-target", "--channel", "all", "--full-type", conflictingOption, Path.Combine(_root, "out.cs"), "--json"]
            : ["source", "source-target", "--channel", "all", "--full-type", conflictingOption, "--json"];

        var exitCode = application.Invoke(arguments, output, error, cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("InvalidOptionCombination", document.RootElement.GetProperty("error").GetProperty("code").GetString());
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
            ["source", "source-target", "--channel", "all", "--context", "0"],
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
            ["source", "source-target", "--channel", "all", "--context", "-1", "--json"],
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
            ["source", "source-target", "--channel", "all", "--file", "--json"],
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
            ["source", "source-target", "--channel", "all", "--file", "--output", destination],
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
            ["source", "source-target", "--channel", "all", "--file", "--output", destination, "--json"],
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
            ["refs", "target", "--channel", "all"],
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
            ["callers", "target", "--channel", "all"],
            callersOutput,
            callersError,
            cancellationToken);

        using var calleesOutput = new StringWriter();
        using var calleesError = new StringWriter();
        var calleesExit = application.Invoke(
            ["callees", "target", "--channel", "all"],
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
            ["refs", "service", "--channel", "all", "--json"],
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

    [Fact]
    public async Task Ambiguous_human_output_renders_numbered_candidate_table_with_total_and_hint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "service", "--channel", "all"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        var text = output.ToString();
        Assert.Contains("Found 2 candidates; showing 2.", text, StringComparison.Ordinal);
        Assert.Contains(
            "1 | Type | Alpha.Service | Alpha.Service | service-a | ScheduleI",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 | Type | Beta.Service | Beta.Service | service-b | ScheduleI",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Hint: re-run with the exact signature or a short ID from the table.",
            text,
            StringComparison.Ordinal);
        var stderr = error.ToString();
        Assert.Contains("Re-run with a full symbol ID or a unique short-ID prefix.", stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    AmbiguousSymbol", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_json_carries_short_ids_suggestions_and_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "service", "--channel", "all", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        var candidates = data.GetProperty("candidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.All(
            candidates.EnumerateArray(),
            item => Assert.Equal(
                item.GetProperty("symbolId").GetString(),
                item.GetProperty("shortId").GetString()));
        Assert.Equal(0, data.GetProperty("suggestions").GetArrayLength());
        Assert.Equal(2, data.GetProperty("totalCandidateCount").GetInt32());
    }

    [Fact]
    public async Task Ambiguous_prefix_overflow_truncates_human_table_with_exact_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixCrowdIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "abcdef00", "--channel", "all"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        var text = output.ToString();
        Assert.Contains("Found 12 candidates; showing 10.", text, StringComparison.Ordinal);
        Assert.Contains("10 | Method |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("11 | Method |", text, StringComparison.Ordinal);
        Assert.Contains("Code:    AmbiguousSymbol", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_prefix_overflow_json_keeps_all_candidates_with_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixCrowdIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "abcdef00", "--channel", "all", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(12, data.GetProperty("candidates").GetArrayLength());
        Assert.Equal(12, data.GetProperty("totalCandidateCount").GetInt32());
        Assert.Equal(
            "abcdef0000ff",
            data.GetProperty("candidates")[0].GetProperty("shortId").GetString());
    }

    [Fact]
    public async Task NotFound_human_output_lists_nearest_matches_with_search_hint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSuggestionIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "Compote", "--channel", "all"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        var text = output.ToString();
        Assert.Contains("Found 0 matches.", text, StringComparison.Ordinal);
        Assert.Contains("Nearest matches:", text, StringComparison.Ordinal);
        Assert.Contains("Demo.Probe.Compute", text, StringComparison.Ordinal);
        Assert.Contains("Demo.Probe.Compare", text, StringComparison.Ordinal);
        Assert.Contains(
            "Hint: no symbol matched 'Compote'; check the spelling or run 's1atlas search \"Compote\"'.",
            text,
            StringComparison.Ordinal);
        var stderr = error.ToString();
        Assert.Contains("No indexed symbol matched the selector.", stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    SymbolNotFound", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFound_json_carries_suggestions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSuggestionIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "Compote", "--channel", "all", "--json"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("SymbolNotFound", root.GetProperty("error").GetProperty("code").GetString());
        var suggestions = root.GetProperty("data").GetProperty("suggestions");
        Assert.Equal(2, suggestions.GetArrayLength());
        Assert.Equal("Demo.Probe.Compute", suggestions[0].GetProperty("qualifiedName").GetString());
        Assert.Equal("Demo.Probe.Compare", suggestions[1].GetProperty("qualifiedName").GetString());
    }

    [Fact]
    public async Task NotFound_without_suggestions_prints_only_the_zero_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["refs", "zzz-no-such-symbol", "--channel", "all"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal("Found 0 matches." + Environment.NewLine, output.ToString());
        Assert.Contains("Code:    SymbolNotFound", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_human_rows_show_short_ids()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedShortIdSearchIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["search", "Widget", "--channel", "all", "--limit", "10"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        var fullId = "abcdef123456" + new string('7', 52);
        Assert.Contains("abcdef123456", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fullId, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_ambiguous_selector_renders_candidate_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRelationshipIndexAsync(cancellationToken);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["source", "service", "--channel", "all"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Found 2 candidates; showing 2.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 | Type | Alpha.Service", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Code:    AmbiguousSymbol", error.ToString(), StringComparison.Ordinal);
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
        string? installationRoot = null,
        bool includeContainingType = false,
        int containingTypeStartLine = 1,
        int containingTypeEndLine = 1)
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
        var symbols = new List<IndexSymbolRecord>
        {
            new(
                "source-target",
                snapshotId,
                "ScheduleI:Installed:Method:Demo.SourceTarget::Run()",
                "Method",
                "Demo.SourceTarget.Run",
                "System.Void Demo.SourceTarget::Run()",
                false,
                BodyRecoveryStatus.Recovered)
        };
        var locations = new List<IndexSourceLocationRecord>
        {
            new("source-target", "source-file", startLine, 1, startLine, endColumn)
        };
        if (includeContainingType)
        {
            symbols.Add(new IndexSymbolRecord(
                "source-type",
                snapshotId,
                "ScheduleI:Installed:Type:Demo.SourceTarget",
                "Type",
                "Demo.SourceTarget",
                "Demo.SourceTarget",
                false));
            locations.Add(new IndexSourceLocationRecord(
                "source-type",
                "source-file",
                containingTypeStartLine,
                1,
                containingTypeEndLine,
                2));
        }

        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                symbols,
                [new IndexSourceFileRecord("source-file", snapshotId, relativePath, sha256, bytes.LongLength)],
                locations,
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

    private async Task SeedPrefixCrowdIndexAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-prefix-crowd";
        const string indexId = "index-cli-prefix-crowd";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-prefix-crowd",
            "2026-08-14T18:22:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = Enumerable.Range(0, 12)
            .Select(index =>
            {
                var suffix = index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                return new IndexSymbolRecord(
                    "abcdef00" + index.ToString("x2") + "ff" + new string('0', 52),
                    snapshotId,
                    $"ScheduleI:Installed:Method:PrefixProbe.Crowd{suffix}::Run()",
                    "Method",
                    "PrefixProbe.Crowd" + suffix,
                    $"System.Void PrefixProbe.Crowd{suffix}::Run()",
                    false);
            })
            .ToArray();
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T18:23:00Z",
            cancellationToken);
    }

    private async Task SeedSuggestionIndexAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-suggest";
        const string indexId = "index-cli-suggest";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-suggest",
            "2026-08-14T18:22:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(char idFill, string key, string kind, string name) =>
            new(
                new string(idFill, 64),
                snapshotId,
                key,
                kind,
                name,
                "System.Void Demo.Probe::Run()",
                false);
        var symbols = new[]
        {
            Symbol('a', "ScheduleI:Installed:Method:Demo.Probe.Compute::Run()", "Method", "Demo.Probe.Compute"),
            Symbol('b', "ScheduleI:Installed:Method:Demo.Probe.Compare::Run()", "Method", "Demo.Probe.Compare"),
            Symbol('c', "ScheduleI:Installed:Method:Demo.Probe.Alpha::Run()", "Method", "Demo.Probe.Alpha"),
            Symbol('d', "ScheduleI:Installed:Type:Demo.Probe.Widget", "Type", "Demo.Probe.Widget"),
        };
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T18:23:00Z",
            cancellationToken);
    }

    private async Task SeedShortIdSearchIndexAsync(CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-cli-shortid-search";
        const string indexId = "index-cli-shortid-search";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "cli-shortid-search",
            "2026-08-14T18:22:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            new IndexSymbolRecord(
                "abcdef123456" + new string('7', 52),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Shop.Widget::Run()",
                "Method",
                "Demo.Shop.Widget",
                "System.Void Demo.Shop.Widget::Run()",
                false),
            new IndexSymbolRecord(
                "gadget-1",
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Shop.Gadget::Run()",
                "Method",
                "Demo.Shop.Gadget",
                "System.Void Demo.Shop.Gadget::Run()",
                false),
        };
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
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

    private static string CreateRuntimeSource() => string.Join(
        '\n',
        "line-1",
        "line-2",
        "line-3",
        "line-4",
        "line-5",
        "public void Run() { var collider = new Collider(); }",
        "line-7",
        "line-8",
        "line-9",
        "line-10",
        "line-11",
        "line-12");

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed record SeededSource(string Sha256);
}
