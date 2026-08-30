using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.IntegrationTests.Indexing;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class SeamInvestigationCliTests
{
    [Theory]
    [InlineData("", "Question", "InvalidSelector")]
    [InlineData("   ", "Question", "InvalidSelector")]
    [InlineData("Alpha", "", "InvalidQuestion")]
    [InlineData("Alpha", "   ", "InvalidQuestion")]
    public async Task Investigate_seam_rejects_blank_selector_and_question_values(
        string selector,
        string question,
        string expectedCode)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run(
            "investigate_seam",
            selector,
            "--question",
            question,
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, expectedCode);
    }

    [Theory]
    [InlineData("0", "InvalidOwnerLimit")]
    [InlineData("51", "InvalidOwnerLimit")]
    public async Task Investigate_seam_rejects_out_of_range_owner_limits(
        string ownerLimit,
        string expectedCode)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--owner-limit",
            ownerLimit,
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, expectedCode);
    }

    [Fact]
    public async Task Investigate_seam_rejects_negative_context()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--context",
            "-1",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "InvalidContext");
    }

    [Fact]
    public async Task Investigate_seam_accepts_native_lookup_options_and_bounds_the_budget()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var invalid = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--native-symbol-id",
            "native-target",
            "--native-traversal-budget",
            "501",
            "--json");

        Assert.Equal(1, invalid.ExitCode);
        Assert.Equal(string.Empty, invalid.StandardError);
        AssertJsonErrorCode(invalid.StandardOutput, "InvalidNativeTraversalBudget");
    }

    [Theory]
    [InlineData("reference", null)]
    [InlineData("all", null)]
    [InlineData("game", "qol")]
    public async Task Investigate_seam_rejects_invalid_scope_collection_combinations(
        string scope,
        string? collection)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();
        var args = new List<string>
        {
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--scope",
            scope,
            "--json"
        };
        if (collection is not null)
        {
            args.Add("--collection");
            args.Add(collection);
        }

        var result = atlas.Run(args.ToArray());

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "InvalidOptionCombination");
    }

    [Fact]
    public async Task Investigate_seam_returns_an_insufficient_coverage_success_packet_with_human_summary_before_details()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--build",
            "build-oc32",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--details");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        var text = result.StandardOutput;
        Assert.Contains("Conclusion:", text, StringComparison.Ordinal);
        Assert.Contains("InsufficientCoverage", text, StringComparison.Ordinal);
        Assert.Contains("Candidate:", text, StringComparison.Ordinal);
        Assert.Contains("Game.Clearing.ClearGeneric", text, StringComparison.Ordinal);
        Assert.Contains("Coverage warnings:", text, StringComparison.Ordinal);
        Assert.Contains("Owner candidates:", text, StringComparison.Ordinal);
        Assert.Contains("generic-clearing", text, StringComparison.Ordinal);
        Assert.Contains("Unknown dimensions:", text, StringComparison.Ordinal);
        Assert.Contains("Next actions:", text, StringComparison.Ordinal);
        Assert.Contains("Claims:", text, StringComparison.Ordinal);
        Assert.Contains("Evidence sections:", text, StringComparison.Ordinal);

        Assert.True(
            text.IndexOf("Conclusion:", StringComparison.Ordinal) <
            text.IndexOf("Claims:", StringComparison.Ordinal),
            text);
        Assert.True(
            text.IndexOf("Next actions:", StringComparison.Ordinal) <
            text.IndexOf("Evidence sections:", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public async Task Investigate_seam_json_is_deterministic_across_repeated_runs()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var first = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--json");
        var second = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--json");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal(string.Empty, first.StandardError);
        Assert.Equal(string.Empty, second.StandardError);
    }

    [Fact]
    public async Task Investigate_seam_parity_between_summary_and_details_preserves_core_fields_and_gate_records()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var summary = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--json");
        var details = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--details",
            "--json");

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(0, details.ExitCode);
        Assert.Equal(string.Empty, summary.StandardError);
        Assert.Equal(string.Empty, details.StandardError);

        using var summaryDocument = JsonDocument.Parse(summary.StandardOutput);
        using var detailsDocument = JsonDocument.Parse(details.StandardOutput);
        var summaryData = summaryDocument.RootElement.GetProperty("data");
        var detailsData = detailsDocument.RootElement.GetProperty("data");

        Assert.Equal("investigate_seam", summaryDocument.RootElement.GetProperty("command").GetString());
        Assert.Equal("investigate_seam", detailsDocument.RootElement.GetProperty("command").GetString());
        AssertJsonObjectsEquivalent(
            summaryData,
            detailsData,
            ["claims", "evidenceSections"]);
        AssertJsonObjectsEquivalent(
            summaryData.GetProperty("pinnedProvenance"),
            detailsData.GetProperty("pinnedProvenance"));
        AssertJsonObjectsEquivalent(
            summaryData.GetProperty("authorityEntityAttribution"),
            detailsData.GetProperty("authorityEntityAttribution"));
        AssertJsonObjectsEquivalent(
            summaryData.GetProperty("alternateGenericCallersAndExclusivity"),
            detailsData.GetProperty("alternateGenericCallersAndExclusivity"));
        AssertJsonObjectsEquivalent(
            summaryData.GetProperty("lifecyclePositionAndBeforeAfterState"),
            detailsData.GetProperty("lifecyclePositionAndBeforeAfterState"));
        AssertJsonObjectsEquivalent(
            summaryData.GetProperty("apiBeforePatchResult"),
            detailsData.GetProperty("apiBeforePatchResult"));

        Assert.Empty(summaryData.GetProperty("claims").EnumerateArray());
        Assert.Empty(summaryData.GetProperty("evidenceSections").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("claims").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("evidenceSections").EnumerateArray());

        AssertRequiredGateMetadataPresent(summaryData);
        AssertRequiredGateMetadataPresent(detailsData);
    }

    [Fact]
    public async Task Investigate_seam_returns_a_resolved_insufficient_coverage_json_result_with_exit_code_zero()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0",
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("investigate_seam", root.GetProperty("command").GetString());

        var data = root.GetProperty("data");
        Assert.Equal("Which seam owns settlement clearing?", data.GetProperty("behavioralQuestion").GetString());
        Assert.Equal("InsufficientCoverage", data.GetProperty("conclusion").GetString());
        Assert.Equal("Resolved", data.GetProperty("resolution").GetProperty("status").GetString());
        Assert.Equal("Game.Clearing.ClearGeneric", data.GetProperty("candidate").GetProperty("qualifiedName").GetString());
        Assert.Equal("generic-clearing", data.GetProperty("candidate").GetProperty("symbolId").GetString());
        Assert.Equal("index-oc32", data.GetProperty("resolution").GetProperty("symbol").GetProperty("indexId").GetString());
        Assert.Equal(
            ["generic-clearing", "free-release", "request-boundary"],
            data.GetProperty("ownerCandidates")
                .EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("symbolId").GetString()!)
                .ToArray());
        var coverageWarnings = data
            .GetProperty("coverageWarnings")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Contains("Escalation: API before-patch coverage is unavailable", coverageWarnings);
        Assert.Contains("Escalation: lifecycle position and before/after state are unavailable", coverageWarnings);
        Assert.Contains("Escalation: unresolved owning authority", coverageWarnings);
        var unknownDimensions = data.GetProperty("unknownDimensions").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("authority/entity attribution", unknownDimensions);
        Assert.Contains("exclusivity", unknownDimensions);
        Assert.Contains("lifecycle position and before/after state", unknownDimensions);
        Assert.Contains("api coverage", unknownDimensions);
        var nextActions = data.GetProperty("nextActions").EnumerateArray().Select(item => item.GetProperty("kind").GetString()!).ToArray();
        Assert.Contains("api-lookup", nextActions);
        Assert.Contains("runtime-proof", nextActions);
        Assert.All(
            data.GetProperty("ownerCandidates").EnumerateArray(),
            item => Assert.Equal("index-oc32", item.GetProperty("symbol").GetProperty("indexId").GetString()));
        AssertRequiredGateMetadataPresent(data);
        var pinnedProvenance = data.GetProperty("pinnedProvenance");
        Assert.Equal("build-oc32", pinnedProvenance.GetProperty("requestedBuildId").GetString());
        Assert.Equal("build-oc32", pinnedProvenance.GetProperty("resolvedBuildId").GetString());
        Assert.Equal(atlas.ExpectedExtractionId, pinnedProvenance.GetProperty("extractionId").GetString());
        Assert.Equal("index-oc32", pinnedProvenance.GetProperty("indexId").GetString());
        Assert.True(pinnedProvenance.GetProperty("integrityVerified").GetBoolean());
        Assert.Equal(
            "ScheduleI",
            pinnedProvenance.GetProperty("codebase").GetString());
        Assert.True(data.TryGetProperty("referenceCollectionBaseProvenance", out var baseProvenance));
        Assert.Equal(JsonValueKind.Null, baseProvenance.ValueKind);
    }

    [Fact]
    public async Task Investigate_seam_returns_a_resolved_no_supportable_seam_success_for_complete_evidence()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateNoSupportableSeamAsync();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns the complete-evidence target?",
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        var data = root.GetProperty("data");
        Assert.Equal("InsufficientCoverage", data.GetProperty("conclusion").GetString());
        Assert.Equal("Resolved", data.GetProperty("resolution").GetProperty("status").GetString());
        Assert.All(
            data.GetProperty("evidenceSections").EnumerateArray(),
            section => Assert.Equal("Complete", section.GetProperty("coverage").GetString()));
        AssertRequiredGateMetadataPresent(data);
        var apiBeforePatch = data.GetProperty("apiBeforePatchResult");
        Assert.Equal("UNKNOWN", apiBeforePatch.GetProperty("apiSurface").GetString());
        Assert.Equal("Unavailable", apiBeforePatch.GetProperty("coverage").GetString());
        Assert.Equal(
            "API-before-patch evidence is unavailable for the selected non-callable seam.",
            apiBeforePatch.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Investigate_seam_returns_ambiguous_symbol_failures_without_wrapping_them_as_success()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateAmbiguousAsync();

        var result = atlas.Run(
            "investigate_seam",
            "Game.Seams.Ambiguous.Run",
            "--question",
            "Which seam owns the ambiguous path?",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("AmbiguousSymbol", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            2,
            root.GetProperty("data").GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task Investigate_seam_returns_no_completed_index_when_the_selected_authority_has_not_been_indexed()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateNoCompletedIndexAsync();

        var result = atlas.Run(
            "investigate_seam",
            "Game.Seams.Missing.Run",
            "--question",
            "Which seam owns the missing path?",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "NoCompletedIndex",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Investigate_seam_rejects_out_of_range_relationship_limits_with_a_machine_stable_code()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "0",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "InvalidRelationshipLimit",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Investigate_seam_rejects_unavailable_installed_authority()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateOc32Async();

        var result = atlas.Run(
            "investigate_seam",
            atlas.TargetSymbolId,
            "--question",
            "Which seam owns settlement clearing?",
            "--build",
            "build-missing",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "BuildNotFound");
    }

    [Fact]
    public async Task Investigate_seam_reference_scope_pins_the_resolved_base_build_authority()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);
        using var indexedDocument = JsonDocument.Parse(indexed.StandardOutput);
        var referenceIndexId = indexedDocument.RootElement.GetProperty("data").GetProperty("indexId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(referenceIndexId));

        var dataRoot = Directory.GetParent(Directory.GetParent(atlas.ModRoot)!.FullName)!.FullName;
        var repository = new SqliteAtlasRepository(
            Path.Combine(dataRoot, "atlas.db"),
            Path.Combine(dataRoot, "backups"));
        var expectedExtraction = await repository.GetPreferredExtractionAsync(
            "build-current",
            TestContext.Current.CancellationToken);
        Assert.NotNull(expectedExtraction);
        var expectedIndex = await repository.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            expectedExtraction.ExtractionId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(expectedIndex);

        var result = atlas.Run(
            "investigate_seam",
            "selected/S1Atlas.InteropAssemblyFixture.InteropFixtureRoot::InteropWrapper(System.Int32):System.Int32",
            "--question",
            "Which seam owns the wrapper?",
            "--scope",
            "reference",
            "--collection",
            "qol",
            "--build",
            "build-current",
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var pinnedProvenance = document.RootElement.GetProperty("data").GetProperty("pinnedProvenance");
        Assert.Equal("build-current", pinnedProvenance.GetProperty("requestedBuildId").GetString());
        Assert.Equal("build-current", pinnedProvenance.GetProperty("resolvedBuildId").GetString());
        Assert.Null(pinnedProvenance.GetProperty("extractionId").GetString());
        Assert.Equal(referenceIndexId, pinnedProvenance.GetProperty("indexId").GetString());
        Assert.False(pinnedProvenance.GetProperty("integrityVerified").GetBoolean());
        var baseProvenance = document.RootElement.GetProperty("data").GetProperty("referenceCollectionBaseProvenance");
        Assert.Equal("build-current", baseProvenance.GetProperty("requestedBuildId").GetString());
        Assert.Equal("build-current", baseProvenance.GetProperty("resolvedBuildId").GetString());
        Assert.Equal(expectedExtraction.ExtractionId, baseProvenance.GetProperty("extractionId").GetString());
        Assert.Equal(expectedIndex.IndexId, baseProvenance.GetProperty("indexId").GetString());
        Assert.Equal("ScheduleI", baseProvenance.GetProperty("codebase").GetString());
        Assert.Equal("Installed", baseProvenance.GetProperty("channel").GetString());
        Assert.True(baseProvenance.GetProperty("integrityVerified").GetBoolean());
    }

    [Fact]
    public async Task Investigate_seam_scoped_queries_reject_mismatched_explicit_builds()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);

        var result = atlas.Run(
            "investigate_seam",
            "InteropWrapper",
            "--question",
            "Which seam owns the wrapper?",
            "--scope",
            "all",
            "--collection",
            "qol",
            "--build",
            CliParityAtlas.ExplicitBuildId,
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "ReferenceCollectionBuildMismatch");
    }

    [Fact]
    public async Task Investigate_seam_scoped_queries_reject_reference_collection_base_index_mismatch()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);
        using var indexedJson = JsonDocument.Parse(indexed.StandardOutput);
        var referenceIndexId = indexedJson.RootElement.GetProperty("data").GetProperty("indexId").GetString()!;
        await atlas.SetReferenceBaseIndexAsync(referenceIndexId, "index-newer", "snapshot-index-newer");

        var result = atlas.Run(
            "investigate_seam",
            "InteropWrapper",
            "--question",
            "Which seam owns the wrapper?",
            "--scope",
            "all",
            "--collection",
            "qol",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "ReferenceCollectionBaseIndexMismatch");
    }

    private static void AssertJsonErrorCode(string output, string expectedCode)
    {
        using var document = JsonDocument.Parse(output);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertJsonObjectsEquivalent(
        JsonElement expected,
        JsonElement actual,
        IReadOnlyList<string>? ignoreProperties = null)
    {
        var ignored = new HashSet<string>(ignoreProperties ?? Array.Empty<string>());
        foreach (var property in expected.EnumerateObject())
        {
            if (!ignored.Contains(property.Name))
            {
                Assert.True(actual.TryGetProperty(property.Name, out var actualProperty));
                Assert.Equal(property.Value.GetRawText(), actualProperty.GetRawText());
            }
        }
        foreach (var property in actual.EnumerateObject())
        {
            if (!ignored.Contains(property.Name))
            {
                Assert.True(expected.TryGetProperty(property.Name, out _));
            }
        }
    }

    private static void AssertRequiredGateMetadataPresent(JsonElement data)
    {
        Assert.True(data.TryGetProperty("pinnedProvenance", out var pinned) && pinned.ValueKind == JsonValueKind.Object);
        Assert.True(data.TryGetProperty("authorityEntityAttribution", out var attribution) && attribution.ValueKind == JsonValueKind.Object);
        Assert.True(
            data.TryGetProperty("alternateGenericCallersAndExclusivity", out var exclusivity) &&
            exclusivity.ValueKind == JsonValueKind.Object);
        Assert.True(
            data.TryGetProperty("lifecyclePositionAndBeforeAfterState", out var lifecycle) &&
            lifecycle.ValueKind == JsonValueKind.Object);
        Assert.True(
            data.TryGetProperty("apiBeforePatchResult", out var apiBeforePatch) &&
            apiBeforePatch.ValueKind == JsonValueKind.Object);

        Assert.Equal(JsonValueKind.String, pinned.GetProperty("indexId").ValueKind);
        Assert.Equal(JsonValueKind.String, pinned.GetProperty("codebase").ValueKind);
        Assert.Equal(JsonValueKind.String, pinned.GetProperty("channel").ValueKind);

        Assert.Equal(JsonValueKind.String, attribution.GetProperty("authority").ValueKind);
        Assert.Equal(JsonValueKind.String, attribution.GetProperty("entity").ValueKind);
        Assert.Equal(JsonValueKind.Array, attribution.GetProperty("evidenceIds").ValueKind);

        Assert.Equal(JsonValueKind.Array, exclusivity.GetProperty("callers").ValueKind);
        Assert.True(
            exclusivity.GetProperty("isExclusive").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Expected isExclusive to be a boolean value.");
        Assert.Equal(JsonValueKind.String, exclusivity.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Array, exclusivity.GetProperty("evidenceIds").ValueKind);

        Assert.Equal(JsonValueKind.String, lifecycle.GetProperty("position").ValueKind);
        Assert.Equal(JsonValueKind.String, lifecycle.GetProperty("beforeState").ValueKind);
        Assert.Equal(JsonValueKind.String, lifecycle.GetProperty("afterState").ValueKind);
        Assert.Equal(JsonValueKind.String, lifecycle.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Array, lifecycle.GetProperty("evidenceIds").ValueKind);

        Assert.Equal(JsonValueKind.String, apiBeforePatch.GetProperty("apiSurface").ValueKind);
        Assert.Equal(JsonValueKind.String, apiBeforePatch.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.String, apiBeforePatch.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Array, apiBeforePatch.GetProperty("evidenceIds").ValueKind);
    }
}

internal sealed class SeamInvestigationCliAtlas : IAsyncDisposable
{
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-29T00:00:00Z");
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private SeamInvestigationCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }
    public string TargetSymbolId { get; private set; } = string.Empty;
    public string ExpectedExtractionId { get; private set; } = string.Empty;

    public static async Task<SeamInvestigationCliAtlas> CreateOc32Async()
    {
        var atlas = await CreateEmptyAsync("oc32");
        var target = await atlas.SeedOc32FixtureAsync();
        atlas.TargetSymbolId = target.SymbolId;
        return atlas;
    }

    public static async Task<SeamInvestigationCliAtlas> CreateNoSupportableSeamAsync()
    {
        var atlas = await CreateEmptyAsync("no-supportable-seam");
        var target = await atlas.SeedNoSupportableSeamFixtureAsync();
        atlas.TargetSymbolId = target.SymbolId;
        return atlas;
    }

    public static Task<SeamInvestigationCliAtlas> CreateBareAsync() =>
        CreateEmptyAsync("bare");

    public static async Task<SeamInvestigationCliAtlas> CreateAmbiguousAsync()
    {
        var atlas = await CreateEmptyAsync("ambiguous");
        await atlas.SeedAmbiguousFixtureAsync();
        return atlas;
    }

    public static async Task<SeamInvestigationCliAtlas> CreateNoCompletedIndexAsync()
    {
        var atlas = await CreateEmptyAsync("no-index");
        await atlas.SeedValidatedExtractionOnlyAsync("build-no-index");
        return atlas;
    }

    public (int ExitCode, string StandardOutput, string StandardError) Run(params string[] args)
    {
        var application = new CliApplication(DataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static async Task<SeamInvestigationCliAtlas> CreateEmptyAsync(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-seam-cli-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        var atlas = new SeamInvestigationCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(CancellationToken.None);
        await atlas.SeedToolInstanceAsync();
        return atlas;
    }

    private async Task<IndexSymbolRecord> SeedOc32FixtureAsync()
    {
        const string buildId = "build-oc32";
        await SeedValidatedExtractionOnlyAsync(buildId);

        var target = Method("target-oc32", "snapshot-oc32", "Game.Seams.Target.Run", BodyRecoveryStatus.Recovered);
        var requestBoundary = Method("request-boundary", "snapshot-oc32", "Game.RequestBoundary.HandleSettlementRequest", BodyRecoveryStatus.Recovered);
        var genericClearing = Method("generic-clearing", "snapshot-oc32", "Game.Clearing.ClearGeneric", BodyRecoveryStatus.Recovered);
        var freeRelease = Method("free-release", "snapshot-oc32", "Game.Free_Server.Release", BodyRecoveryStatus.Recovered);
        var uiSettlement = Method("ui-settlement", "snapshot-oc32", "UI.SettlementPanel.ApplySettlementWithoutPlayer", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            buildId,
            "index-oc32",
            "snapshot-oc32",
            target,
            [requestBoundary, genericClearing, freeRelease, uiSettlement],
            [
                Edge("caller-001-request", "snapshot-oc32", requestBoundary.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-002-generic-clear", "snapshot-oc32", genericClearing.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-003-free-release", "snapshot-oc32", freeRelease.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-004-ui-settlement", "snapshot-oc32", uiSettlement.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return target;
    }

    private async Task<IndexSymbolRecord> SeedNoSupportableSeamFixtureAsync()
    {
        const string buildId = "build-no-supportable-seam";
        const string snapshotId = "snapshot-no-supportable-seam";
        await SeedValidatedExtractionOnlyAsync(buildId);

        var target = Type("target-no-supportable", snapshotId, "Game.Seams.CompleteEvidenceTarget");
        var requestOwner = Method(
            "complete-request-owner",
            snapshotId,
            "Game.RequestBoundary.HandleCompleteEvidence",
            BodyRecoveryStatus.Recovered);
        var releaseOwner = Method(
            "complete-release-owner",
            snapshotId,
            "Game.Free_Server.ReleaseCompleteEvidence",
            BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            buildId,
            "index-no-supportable-seam",
            snapshotId,
            target,
            [requestOwner, releaseOwner],
            [
                Edge("complete-caller-request", snapshotId, requestOwner.SymbolId, target.SymbolId, null, "Calls"),
                Edge("complete-caller-release", snapshotId, releaseOwner.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: false);

        return target;
    }

    private async Task SeedAmbiguousFixtureAsync()
    {
        const string buildId = "build-ambiguous";
        await SeedValidatedExtractionOnlyAsync(buildId);

        var targetA = Method("ambiguous-a", "snapshot-ambiguous", "Game.Seams.Ambiguous.Run", BodyRecoveryStatus.Recovered);
        var targetB = new IndexSymbolRecord(
            "ambiguous-b",
            "snapshot-ambiguous",
            "ScheduleI:Installed:Method:Game.Seams.Ambiguous::Run(System.Int32)",
            "Method",
            "Game.Seams.Ambiguous.Run",
            "System.Void Game.Seams.Ambiguous::Run(System.Int32)",
            false,
            BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            buildId,
            "index-ambiguous",
            "snapshot-ambiguous",
            targetA,
            [targetB],
            [],
            includeCallableSurface: false);
    }

    private async Task SeedValidatedExtractionOnlyAsync(string buildId)
    {
        await SeedSnapshotAsync(buildId);
        var extractionId = await SeedValidatedExtractionAsync(buildId, SeedForBuild(buildId));
        ExpectedExtractionId = extractionId;
        await _repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                extractionId,
                BaseTime.AddMinutes(2),
                ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
    }

    private async Task<string> SeedValidatedExtractionAsync(string buildId, string seed)
    {
        var recipeId = seed.PadLeft(64, seed[0]);
        var manifest = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/Assembly-CSharp.dll",
                ArtifactKind.ManagedAssembly,
                6,
                Convert.ToHexString(SHA256.HashData([10, 20, 30, 40, 50, 60])).ToLowerInvariant(),
                "Assembly-CSharp",
                "Assembly-CSharp.dll",
                1,
                1,
                0,
                0,
                0)
        ]);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var attempt = await CreateValidatingAttemptAsync(buildId, recipeId, extractionId[..32]);
        var statistics = new ExtractionStatistics(
            1,
            1,
            1,
            1,
            1,
            0,
            0,
            0,
            6,
            6,
            [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 6, 1, 1, 0, 0, 0)]);
        var extractionRoot = Path.Combine(DataRoot, "builds", buildId, "extractions", extractionId);
        var extraction = new ValidatedExtraction(
            extractionId,
            recipeId,
            buildId,
            ToolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            ProfileDigest,
            1,
            1,
            digest,
            extractionRoot,
            BaseTime.AddMinutes(1),
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            statistics);
        var report = new ValidationReport(
            1,
            attempt.AttemptId,
            ValidationSubjectKind.CandidateOutput,
            null,
            buildId,
            recipeId,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            ValidationOutcome.Valid,
            true,
            true,
            true,
            digest,
            statistics,
            null,
            [],
            [],
            true,
            BaseTime.AddMinutes(2));
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        await File.WriteAllBytesAsync(
            Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"),
            [10, 20, 30, 40, 50, 60]);
        await WriteValidatedExtractionDocumentsAsync(extractionRoot, extraction, manifest, report);
        await _repository.CommitValidatedExtractionAsync(
            new ValidatedExtractionPromotion(
                attempt with
                {
                    Status = ExtractionAttemptStatus.Succeeded,
                    CompletedAtUtc = BaseTime.AddMinutes(2),
                    ResultExtractionId = extractionId
                },
                extraction,
                manifest,
                report,
                null),
            CancellationToken.None);
        return extractionId;
    }

    private async Task CompleteGameRunAsync(
        string buildId,
        string indexId,
        string snapshotId,
        IndexSymbolRecord target,
        IReadOnlyList<IndexSymbolRecord> additionalSymbols,
        IReadOnlyList<IndexRelationshipRecord> relationships,
        bool includeCallableSurface)
    {
        var extractionId = (await _repository.GetPreferredExtractionAsync(buildId, TestContext.Current.CancellationToken))!.ExtractionId;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            BaseTime.AddMinutes(3).ToString("O"));
        await _repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            TestContext.Current.CancellationToken);

        var sourceText = "namespace Game.Seams;\npublic class Target\n{\n    public void Run()\n    {\n        return;\n    }\n}\n";
        var sourceFile = new IndexSourceFileRecord(
            "file-" + indexId,
            snapshotId,
            "Assembly-CSharp.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var sourceLocation = new IndexSourceLocationRecord(target.SymbolId, sourceFile.SourceFileId, 4, 5, 7, 6);

        var symbols = new List<IndexSymbolRecord> { target };
        symbols.AddRange(additionalSymbols);
        var writeSet = new IndexWriteSet(
            symbols,
            [sourceFile],
            [sourceLocation],
            [],
            relationships,
            includeCallableSurface
                ? [
                    new IndexCallableSurfaceRecord(
                        "surface-" + indexId,
                        indexId,
                        snapshotId,
                        target.SymbolId,
                        target.CanonicalKey,
                        "Assembly-CSharp.dll",
                        "interop-" + indexId,
                        target.Signature,
                        CallableSurfaceKind.PublicMethodWrapper,
                        false,
                        CallableSurfaceStatus.Resolved,
                        InteropInputTrust.LocalOnly,
                        "wrapper forwards through il2cpp_runtime_invoke")
                ]
                : []);
        await _repository.CompleteIndexRunAsync(
            indexId,
            writeSet,
            BaseTime.AddMinutes(4).ToString("O"),
            TestContext.Current.CancellationToken);

        var indexRoot = Path.Combine(DataRoot, "builds", buildId, "indexes", indexId);
        Directory.CreateDirectory(indexRoot);
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, sourceFile.RelativePath),
            sourceText,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
    }

    private async Task<ExtractionAttempt> CreateValidatingAttemptAsync(string buildId, string recipeId, string attemptId)
    {
        var created = new ExtractionAttempt(
            attemptId,
            recipeId,
            buildId,
            ToolInstanceId,
            "default-profile",
            1,
            ProfileDigest,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            1,
            1,
            ExtractionInputSource.Live,
            null,
            ExtractionAttemptStatus.Created,
            BaseTime,
            null,
            null,
            null,
            null,
            $"C:\\attempts\\{attemptId}\\work",
            $"C:\\attempts\\{attemptId}\\stdout.log",
            $"C:\\attempts\\{attemptId}\\stderr.log",
            false,
            false,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            false,
            0,
            0,
            null,
            null);
        await _repository.CreateAttemptAsync(created, CancellationToken.None);
        var preparing = created with { Status = ExtractionAttemptStatus.Preparing, StartedAtUtc = BaseTime };
        await _repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, CancellationToken.None);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 1234 };
        await _repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, CancellationToken.None);
        var completed = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = "C:\\candidate"
        };
        await _repository.TransitionAttemptAsync(completed, ExtractionAttemptStatus.Running, CancellationToken.None);
        var validating = completed with { Status = ExtractionAttemptStatus.Validating };
        await _repository.TransitionAttemptAsync(validating, ExtractionAttemptStatus.ProcessCompleted, CancellationToken.None);
        return validating;
    }

    private Task SeedSnapshotAsync(string buildId) =>
        _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, BaseTime, true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                BaseTime),
            CancellationToken.None);

    private async Task SeedToolInstanceAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DataRoot, "atlas.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO tool_instances (tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ($id, 'cpp2il', 'test', 'win-x64', 'ManagedPinned', 'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe', '2026-08-29T00:00:00.0000000+00:00', '2026-08-29T00:05:00.0000000+00:00', 'Verified');""";
        command.Parameters.AddWithValue("$id", ToolInstanceId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteValidatedExtractionDocumentsAsync(
        string extractionRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report)
    {
        var extractionAssembly = typeof(ValidatedExtractionIntegrityVerifier).Assembly;
        var storeType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionDocumentStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(storeType)
            ?? throw new InvalidOperationException("Could not create validated extraction document store.");
        var writeMethod = storeType.GetMethod("WriteFinalDocumentsAsync")
            ?? throw new InvalidOperationException("Validated extraction document writer was not found.");
        var writeTask = (Task)writeMethod.Invoke(
            store,
            [DataRoot, extractionRoot, extraction, manifest, report, CancellationToken.None])!;
        await writeTask;
    }

    private static IndexSymbolRecord Method(
        string id,
        string snapshotId,
        string qualifiedName,
        BodyRecoveryStatus status)
    {
        var member = CanonicalMember(qualifiedName);
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + member,
            "Method",
            qualifiedName,
            "System.Void " + member,
            false,
            status);
    }

    private static IndexSymbolRecord Type(
        string id,
        string snapshotId,
        string qualifiedName) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Type:" + qualifiedName,
            "Type",
            qualifiedName,
            qualifiedName,
            false);

    private static IndexRelationshipRecord Edge(
        string id,
        string snapshotId,
        string source,
        string? target,
        string? targetText,
        string kind) =>
        new(id, snapshotId, source, target, targetText, kind, "fixture:" + id);

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName + "()"
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..] + "()";
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SeedForBuild(string buildId) =>
        buildId switch
        {
            "build-oc32" => "1",
            "build-ambiguous" => "2",
            "build-no-index" => "3",
            _ => "4"
        };
}
