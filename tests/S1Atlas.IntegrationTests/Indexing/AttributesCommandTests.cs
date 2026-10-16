using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class AttributesCommandTests
{
    [Fact]
    public async Task Attributes_full_name_query_lists_matching_symbols_with_arguments()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "FishNet.Object.ServerRpcAttribute");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("Found 2 matches. Showing 2.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Demo.Net.ServerRpcA.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Demo.Net.ServerRpcB.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.Other.Handler.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "[FishNet.Object.ServerRpcAttribute(42, RequireOwnership=false)]",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "[FishNet.Object.ServerRpcAttribute(7, RequireOwnership=true)]",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_suffix_query_matches_after_last_dot()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "ServerRpcAttribute");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Found 3 matches. Showing 3.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Demo.Other.Handler.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[Other.ServerRpcAttribute()]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_named_arg_filter_matches_name_and_canonical_value()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run(
            "attributes",
            "FishNet.Object.ServerRpcAttribute",
            "--arg",
            "RequireOwnership=false");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Demo.Net.ServerRpcA.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.Net.ServerRpcB.Run", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_bare_arg_filter_matches_any_fixed_argument()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "FishNet.Object.ServerRpcAttribute", "--arg", "7");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Demo.Net.ServerRpcB.Run", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.Net.ServerRpcA.Run", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_arg_filters_combine_with_and()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var matching = atlas.Run(
            "attributes",
            "FishNet.Object.ServerRpcAttribute",
            "--arg",
            "RequireOwnership=false",
            "--arg",
            "42");
        var conflicting = atlas.Run(
            "attributes",
            "FishNet.Object.ServerRpcAttribute",
            "--arg",
            "RequireOwnership=false",
            "--arg",
            "7");

        Assert.Equal(0, matching.ExitCode);
        Assert.Contains("Demo.Net.ServerRpcA.Run", matching.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, conflicting.ExitCode);
        Assert.Contains("Found 0 matches. Showing 0.", conflicting.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_json_shape_includes_symbol_and_decoded_arguments()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "FishNet.Object.ServerRpcAttribute", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, data.GetProperty("returnedCount").GetInt32());
        var hits = data.GetProperty("attributeHits").EnumerateArray().ToArray();
        Assert.Equal(2, hits.Length);
        Assert.Equal(
            "Demo.Net.ServerRpcA.Run",
            hits[0].GetProperty("symbol").GetProperty("qualifiedName").GetString());
        var attribute = Assert.Single(hits[0].GetProperty("matchingAttributes").EnumerateArray());
        Assert.Equal("FishNet.Object.ServerRpcAttribute", attribute.GetProperty("attributeType").GetString());
        Assert.Equal("Member", attribute.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, attribute.GetProperty("parameterIndex").ValueKind);
        var fixedArgument = Assert.Single(attribute.GetProperty("fixedArguments").EnumerateArray());
        Assert.Equal(42, fixedArgument.GetProperty("value").GetInt32());
        var namedArgument = Assert.Single(attribute.GetProperty("namedArguments").EnumerateArray());
        Assert.Equal("RequireOwnership", namedArgument.GetProperty("name").GetString());
        Assert.False(namedArgument.GetProperty("value").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task Attributes_json_reports_parameter_target_with_index_and_name()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "Demo.Annotations.ValidateAttribute", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var hits = document.RootElement.GetProperty("data").GetProperty("attributeHits").EnumerateArray().ToArray();
        var hit = Assert.Single(hits);
        Assert.Equal("Demo.Params.Echo", hit.GetProperty("symbol").GetProperty("qualifiedName").GetString());
        var attribute = Assert.Single(hit.GetProperty("matchingAttributes").EnumerateArray());
        Assert.Equal("Parameter", attribute.GetProperty("target").GetString());
        Assert.Equal(0, attribute.GetProperty("parameterIndex").GetInt32());
        Assert.Equal("value", attribute.GetProperty("parameterName").GetString());
    }

    [Fact]
    public async Task Attributes_codebase_s1api_routes_to_the_installed_api_index()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run(
            "attributes",
            "System.ObsoleteAttribute",
            "--codebase",
            "s1api",
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var hits = document.RootElement.GetProperty("data").GetProperty("attributeHits").EnumerateArray().ToArray();
        var hit = Assert.Single(hits);
        Assert.Equal("Demo.Api.Legacy", hit.GetProperty("symbol").GetProperty("qualifiedName").GetString());
        Assert.Equal("S1Api", hit.GetProperty("symbol").GetProperty("codebase").GetString());
        var attribute = Assert.Single(hit.GetProperty("matchingAttributes").EnumerateArray());
        Assert.Equal("System.ObsoleteAttribute", attribute.GetProperty("attributeType").GetString());
        var fixedArgument = Assert.Single(attribute.GetProperty("fixedArguments").EnumerateArray());
        Assert.Equal("Use New instead", fixedArgument.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Attributes_include_generated_flag_controls_generated_symbols()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var hidden = atlas.Run("attributes", "FishNet.Object.ServerRpcAttribute");
        var shown = atlas.Run("attributes", "FishNet.Object.ServerRpcAttribute", "--include-generated");
        var pinnedHidden = atlas.Run(
            "attributes",
            "FishNet.Object.ServerRpcAttribute",
            "--build",
            AttributesCliAtlas.BuildId);
        var pinnedShown = atlas.Run(
            "attributes",
            "FishNet.Object.ServerRpcAttribute",
            "--build",
            AttributesCliAtlas.BuildId,
            "--include-generated");

        Assert.Equal(0, hidden.ExitCode);
        Assert.Contains("Found 2 matches. Showing 2.", hidden.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.Net.GeneratedRpc.Run", hidden.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("Found 3 matches. Showing 3.", shown.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Demo.Net.GeneratedRpc.Run", shown.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "[FishNet.Object.ServerRpcAttribute(9, RequireOwnership=false)]",
            shown.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(0, pinnedHidden.ExitCode);
        Assert.DoesNotContain("Demo.Net.GeneratedRpc.Run", pinnedHidden.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, pinnedShown.ExitCode);
        Assert.Contains("Demo.Net.GeneratedRpc.Run", pinnedShown.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_codebase_s1api_hides_generated_symbols_by_default()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var hidden = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api");
        var shown = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api", "--include-generated");

        Assert.Equal(0, hidden.ExitCode);
        Assert.Contains("Found 1 matches. Showing 1.", hidden.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.Api.GeneratedLegacy", hidden.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("Found 2 matches. Showing 2.", shown.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Demo.Api.GeneratedLegacy", shown.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_absent_api_index_reports_no_completed_index()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var json = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1mapi", "--json");
        var human = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1mapi");

        Assert.Equal(1, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("NoCompletedIndex", error.GetProperty("code").GetString());
        Assert.Contains("No completed S1MApi", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            "s1atlas index --codebase s1mapi --channel installed",
            error.GetProperty("hint").GetString());
        Assert.Equal(1, human.ExitCode);
        Assert.Contains("No completed S1MApi", human.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "Next:    s1atlas index --codebase s1mapi --channel installed",
            human.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_missing_game_index_reports_no_completed_index()
    {
        await using var atlas = await AttributesCliAtlas.CreateEmptyAsync();

        var json = atlas.Run("attributes", "ServerRpcAttribute", "--channel", "release", "--json");
        var human = atlas.Run("attributes", "ServerRpcAttribute", "--channel", "all");

        Assert.Equal(1, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("NoCompletedIndex", error.GetProperty("code").GetString());
        Assert.Contains("No completed index", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("s1atlas index", error.GetProperty("hint").GetString());
        Assert.Equal(1, human.ExitCode);
        Assert.Contains("No completed index", human.StandardError, StringComparison.Ordinal);
        Assert.Contains("Next:    s1atlas index", human.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_stale_api_index_reports_stale_status()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync(staleApiIndex: true);

        var json = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api", "--json");
        var human = atlas.Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api");

        Assert.Equal(1, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("StaleApiIndex", error.GetProperty("code").GetString());
        Assert.Contains("stale", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, human.ExitCode);
        Assert.Contains("StaleApiIndex", human.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_invalid_limit_fails()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "ServerRpcAttribute", "--limit", "0", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("InvalidLimit", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Attributes_unknown_attribute_returns_empty_success()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var json = atlas.Run("attributes", "Does.Not.ExistAttribute", "--json");
        var human = atlas.Run("attributes", "Does.Not.ExistAttribute");

        Assert.Equal(0, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, data.GetProperty("returnedCount").GetInt32());
        Assert.Empty(data.GetProperty("attributeHits").EnumerateArray());
        Assert.Equal(0, human.ExitCode);
        Assert.Contains("Found 0 matches. Showing 0.", human.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_injected_analysis_names_return_no_results()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "Cpp2ILInjected.AddressAttribute", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Empty(data.GetProperty("attributeHits").EnumerateArray());
    }

    [Fact]
    public async Task Attributes_build_selects_the_requested_game_index()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var result = atlas.Run("attributes", "ServerRpcAttribute", "--build", AttributesCliAtlas.BuildId);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Demo.Net.ServerRpcA.Run", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_output_includes_the_symbols_attributes()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var json = atlas.Run("type", "Demo.Widget", "--json");
        var human = atlas.Run("type", "Demo.Widget");

        Assert.Equal(0, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var symbols = document.RootElement.GetProperty("data").GetProperty("symbols").EnumerateArray().ToArray();
        var widget = Assert.Single(symbols, symbol =>
            symbol.GetProperty("qualifiedName").GetString() == "Demo.Widget");
        var attribute = Assert.Single(widget.GetProperty("attributes").EnumerateArray());
        Assert.Equal("System.ObsoleteAttribute", attribute.GetProperty("attributeType").GetString());
        Assert.Equal(0, human.ExitCode);
        Assert.Contains("Demo.Widget", human.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "[System.ObsoleteAttribute(Do not use)]",
            human.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Method_output_includes_the_symbols_attributes()
    {
        await using var atlas = await AttributesCliAtlas.CreateAsync();

        var json = atlas.Run("method", "Demo.Net.ServerRpcA.Run", "--json");
        var human = atlas.Run("method", "Demo.Net.ServerRpcA.Run");

        Assert.Equal(0, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var symbols = document.RootElement.GetProperty("data").GetProperty("symbols").EnumerateArray().ToArray();
        var method = Assert.Single(symbols, symbol =>
            symbol.GetProperty("qualifiedName").GetString() == "Demo.Net.ServerRpcA.Run");
        var attribute = Assert.Single(method.GetProperty("attributes").EnumerateArray());
        Assert.Equal("FishNet.Object.ServerRpcAttribute", attribute.GetProperty("attributeType").GetString());
        var fixedArgument = Assert.Single(attribute.GetProperty("fixedArguments").EnumerateArray());
        Assert.Equal(42, fixedArgument.GetProperty("value").GetInt32());
        Assert.Equal(0, human.ExitCode);
        Assert.Contains(
            "[FishNet.Object.ServerRpcAttribute(42, RequireOwnership=false)]",
            human.StandardOutput,
            StringComparison.Ordinal);
    }
}

internal sealed class AttributesCliAtlas : IAsyncDisposable
{
    public const string BuildId = "build-attributes";
    private const string ToolInstanceId = "tool-attributes";
    private const string ApiDllHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StaleApiDllHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private AttributesCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }

    public static Task<AttributesCliAtlas> CreateEmptyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-attributes-cli-" + Guid.NewGuid().ToString("N"));
        var atlas = new AttributesCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        return Task.FromResult(atlas);
    }

    public static async Task<AttributesCliAtlas> CreateAsync(bool staleApiIndex = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-attributes-cli-" + Guid.NewGuid().ToString("N"));
        var atlas = new AttributesCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas._repository.InitializeAsync(cancellationToken);
        await ExtractionSeed.SeedToolInstanceAsync(atlas.DataRoot, ToolInstanceId, cancellationToken);
        var snapshot = ExtractionSeed.CreateSnapshot(
            BuildId,
            BaseTime,
            dependencies:
            [
                new DependencyVersion(DependencyKind.S1Api, "fixture", "C:\\game\\S1API.dll", true, ApiDllHash),
                new DependencyVersion(DependencyKind.S1Mapi, null, null, false)
            ]);
        await atlas._repository.SaveSnapshotAsync(snapshot, cancellationToken);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            atlas._repository,
            atlas.DataRoot,
            BuildId,
            ExtractionSeed.Sha256("recipe-attributes"),
            ToolInstanceId,
            ExtractionSeed.Sha256("profile-attributes"),
            ExtractionSeed.Sha256("policy-attributes"),
            BaseTime,
            cancellationToken);
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                BuildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);
        await atlas.SeedGameIndexAsync(seeded.Extraction.ExtractionId, cancellationToken);
        await atlas.SeedApiIndexAsync(EnvironmentSnapshotId.Create(snapshot), cancellationToken, staleApiIndex);
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

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task SeedGameIndexAsync(string extractionId, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-attributes-game",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            BaseTime.AddMinutes(11).ToString("O"));
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-attributes-game", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-attributes-game",
            new IndexWriteSet(
                [
                    Method("game-rpc-a", snapshot.SnapshotId, "Demo.Net.ServerRpcA.Run"),
                    Method("game-rpc-b", snapshot.SnapshotId, "Demo.Net.ServerRpcB.Run"),
                    Method("game-other-rpc", snapshot.SnapshotId, "Demo.Other.Handler.Run"),
                    Type("game-widget", snapshot.SnapshotId, "Demo.Widget"),
                    Method("game-param", snapshot.SnapshotId, "Demo.Params.Echo"),
                    Method("game-generated-rpc", snapshot.SnapshotId, "Demo.Net.GeneratedRpc.Run", isGenerated: true)
                ],
                [],
                [],
                [],
                [],
                CustomAttributes:
                [
                    Attribute(
                        "game-attr-001",
                        snapshot.SnapshotId,
                        "game-rpc-a",
                        "Member",
                        null,
                        null,
                        "FishNet.Object.ServerRpcAttribute",
                        [Primitive(42)],
                        [Named("RequireOwnership", Primitive(false))]),
                    Attribute(
                        "game-attr-002",
                        snapshot.SnapshotId,
                        "game-rpc-b",
                        "Member",
                        null,
                        null,
                        "FishNet.Object.ServerRpcAttribute",
                        [Primitive(7)],
                        [Named("RequireOwnership", Primitive(true))]),
                    Attribute(
                        "game-attr-003",
                        snapshot.SnapshotId,
                        "game-other-rpc",
                        "Member",
                        null,
                        null,
                        "Other.ServerRpcAttribute",
                        [],
                        []),
                    Attribute(
                        "game-attr-004",
                        snapshot.SnapshotId,
                        "game-widget",
                        "Member",
                        null,
                        null,
                        "System.ObsoleteAttribute",
                        [StringValue("Do not use")],
                        []),
                    Attribute(
                        "game-attr-005",
                        snapshot.SnapshotId,
                        "game-param",
                        "Parameter",
                        0,
                        "value",
                        "Demo.Annotations.ValidateAttribute",
                        [StringValue("x")],
                        []),
                    Attribute(
                        "game-attr-006",
                        snapshot.SnapshotId,
                        "game-generated-rpc",
                        "Member",
                        null,
                        null,
                        "FishNet.Object.ServerRpcAttribute",
                        [Primitive(9)],
                        [Named("RequireOwnership", Primitive(false))])
                ]),
            BaseTime.AddMinutes(12).ToString("O"),
            cancellationToken);
    }

    private async Task SeedApiIndexAsync(string environmentSnapshotId, CancellationToken cancellationToken, bool stale)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-attributes-api",
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            stale ? StaleApiDllHash : ApiDllHash,
            BaseTime.AddMinutes(13).ToString("O"),
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-attributes-api", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-attributes-api",
            new IndexWriteSet(
                [
                    Method("api-legacy", snapshot.SnapshotId, "Demo.Api.Legacy"),
                    Method("api-generated-legacy", snapshot.SnapshotId, "Demo.Api.GeneratedLegacy", isGenerated: true)
                ],
                [],
                [],
                [],
                [],
                CustomAttributes:
                [
                    Attribute(
                        "api-attr-001",
                        snapshot.SnapshotId,
                        "api-legacy",
                        "Member",
                        null,
                        null,
                        "System.ObsoleteAttribute",
                        [StringValue("Use New instead")],
                        []),
                    Attribute(
                        "api-attr-002",
                        snapshot.SnapshotId,
                        "api-generated-legacy",
                        "Member",
                        null,
                        null,
                        "System.ObsoleteAttribute",
                        [StringValue("Legacy generated")],
                        [])
                ]),
            BaseTime.AddMinutes(14).ToString("O"),
            cancellationToken);
    }

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName, bool isGenerated = false) =>
        new(
            id,
            snapshotId,
            "Fixture:Installed:Method:" + CanonicalMember(qualifiedName),
            "Method",
            qualifiedName,
            "System.Void " + CanonicalMember(qualifiedName) + "()",
            false,
            BodyRecoveryStatus.Recovered,
            IsGenerated: isGenerated);

    private static IndexSymbolRecord Type(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "Fixture:Installed:Type:" + qualifiedName,
            "Type",
            qualifiedName,
            qualifiedName,
            false);

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }

    private static IndexCustomAttributeRecord Attribute(
        string attributeId,
        string snapshotId,
        string symbolId,
        string target,
        int? parameterIndex,
        string? parameterName,
        string attributeType,
        IReadOnlyList<CustomAttributeValue> fixedArguments,
        IReadOnlyList<CustomAttributeNamedArgument> namedArguments) =>
        new(
            attributeId,
            snapshotId,
            symbolId,
            target,
            parameterIndex,
            parameterName,
            attributeType,
            CustomAttributeJson.ToJson(new DecodedCustomAttribute(attributeType, fixedArguments, namedArguments)));

    private static CustomAttributeValue Primitive(object value) =>
        new(
            CustomAttributeValueKind.Primitive,
            value,
            value switch
            {
                int => "System.Int32",
                bool => "System.Boolean",
                _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported test primitive.")
            },
            null);

    private static CustomAttributeValue StringValue(string value) =>
        new(CustomAttributeValueKind.String, value, "System.String", null);

    private static CustomAttributeNamedArgument Named(string name, CustomAttributeValue value) =>
        new(name, value);
}
