using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using S1Atlas.Application.Envelope;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// The unified MCP error vocabulary: every tool error surfaces one of ten
// snake_case codes on the wire, no matter which internal code named it.
public sealed class McpErrorCodeTests : IClassFixture<SharedHealthyServerFixture>, IClassFixture<SharedScenesServerFixture>
{
    private static readonly string[] UnifiedCodes =
    [
        "invalid_arguments",
        "symbol_not_found",
        "no_completed_index",
        "source_unavailable",
        "source_integrity_failure",
        "snapshot_not_found",
        "no_current_build",
        "atlas_unavailable",
        "unexpected_tool_failure",
        "invalid_cursor"
    ];

    private readonly SharedHealthyServerFixture _healthy;
    private readonly SharedScenesServerFixture _scenes;

    public McpErrorCodeTests(SharedHealthyServerFixture healthy, SharedScenesServerFixture scenes)
    {
        _healthy = healthy;
        _scenes = scenes;
    }

    [Fact]
    public async Task AllToolErrorCodes_AreSnakeCaseMembersOfUnifiedVocabulary()
    {
        var codes = new List<string>();

        var tools = await _healthy.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
            codes.AddRange(await CollectSweepCodesAsync(_healthy.Client, tool));

        codes.Add(await CollectCodeAsync(
            _healthy.Client,
            "find_callers",
            new Dictionary<string, object?>
            {
                ["selector"] = _healthy.Atlas.MethodSelector,
                ["codebase"] = "scheduleI",
                ["limit"] = 1,
                ["cursor"] = "bogus"
            }));
        codes.Add(await CollectCodeAsync(
            _healthy.Client,
            "get_type",
            new Dictionary<string, object?>
            {
                ["selector"] = "missing-thing",
                ["codebase"] = "scheduleI"
            }));
        codes.Add(await CollectCodeAsync(
            _healthy.Client,
            "compare_symbol",
            new Dictionary<string, object?>
            {
                ["selector"] = _healthy.Atlas.MethodSelector,
                ["buildIdA"] = "missing-a",
                ["buildIdB"] = "missing-b"
            }));
        codes.Add(await CollectCodeAsync(
            _healthy.Client,
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "anything",
                ["codebase"] = "s1api"
            }));

        await using var empty = await McpTestAtlas.EmptyAsync();
        await using var emptyServer = await McpTestServer.StartAsync(empty.DataRoot, TestContext.Current.CancellationToken);
        var noBuild = await CallToolAsync(emptyServer.Client, "get_environment", new Dictionary<string, object?>());
        codes.Add(RequireCode(noBuild));
        Assert.Equal("s1atlas scan", RequireHint(noBuild));

        var sceneAtlas = _scenes.Atlas;
        codes.Add(await CollectCodeAsync(
            _scenes.Client,
            "get_scene",
            new Dictionary<string, object?>
            {
                ["selector"] = sceneAtlas.SceneNameA,
                ["buildId"] = sceneAtlas.BuildIdA,
                ["sceneSnapshotId"] = "missing-snapshot"
            }));

        Assert.NotEmpty(codes);
        Assert.All(codes, code =>
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", code);
            Assert.Contains(code, UnifiedCodes);
        });
    }

    [Theory]
    [MemberData(nameof(UnifiedMappings))]
    public void InternalCodes_MapIntoUnifiedVocabulary(string internalCode, string wireCode)
    {
        Assert.Contains(wireCode, UnifiedCodes);
        Assert.Equal(wireCode, McpToolErrorCodes.MapToWireCode(internalCode));
    }

    public static TheoryData<string, string> UnifiedMappings() =>
        new()
        {
            { "InvalidArguments", "invalid_arguments" },
            { "InvalidLimit", "invalid_arguments" },
            { "InvalidDepth", "invalid_arguments" },
            { "InvalidOffset", "invalid_arguments" },
            { "InvalidContext", "invalid_arguments" },
            { "InvalidRelatedLimit", "invalid_arguments" },
            { "InvalidRelationshipLimit", "invalid_arguments" },
            { "InvalidOwnerLimit", "invalid_arguments" },
            { "InvalidFieldFilter", "invalid_arguments" },
            { "InvalidOptionCombination", "invalid_arguments" },
            { "InvalidRelationshipKind", "invalid_arguments" },
            { "InvalidKind", "invalid_arguments" },
            { "InvalidScope", "invalid_arguments" },
            { "InvalidCollection", "invalid_arguments" },
            { "CollectionRequired", "invalid_arguments" },
            { "InvalidChannel", "invalid_arguments" },
            { "InvalidQuestion", "invalid_arguments" },
            { "InvalidSelector", "invalid_arguments" },
            { "InvalidNativeTraversalBudget", "invalid_arguments" },
            { "InvalidRuntimeProofRequest", "invalid_arguments" },
            { "InvalidQuery", "invalid_arguments" },
            { "InvalidPage", "invalid_arguments" },
            { "SameBuild", "invalid_arguments" },
            { "SameIndex", "invalid_arguments" },
            { "UnsupportedCodebase", "invalid_arguments" },
            { "UnsupportedContainer", "invalid_arguments" },
            { "UnknownEndpoint", "invalid_arguments" },
            { "SymbolKindMismatch", "invalid_arguments" },
            { "AmbiguousBuildPrefix", "invalid_arguments" },
            { "ReferenceCollectionBuildMismatch", "invalid_arguments" },
            { "ReferenceCollectionBaseIndexMismatch", "invalid_arguments" },
            { "AmbiguousScene", "invalid_arguments" },
            { "AmbiguousGameObject", "invalid_arguments" },
            { "AmbiguousComponent", "invalid_arguments" },
            { "AmbiguousScriptableAsset", "invalid_arguments" },
            { "SymbolNotFound", "symbol_not_found" },
            { "SceneNotFound", "symbol_not_found" },
            { "GameObjectNotFound", "symbol_not_found" },
            { "ComponentNotFound", "symbol_not_found" },
            { "ScriptableAssetNotFound", "symbol_not_found" },
            { "UnresolvedCodeSymbol", "symbol_not_found" },
            { "UnresolvedSceneReference", "symbol_not_found" },
            { "CallableSurfaceUnavailable", "symbol_not_found" },
            { "NoCompletedIndex", "no_completed_index" },
            { "NoCompletedSceneIndex", "no_completed_index" },
            { "NoCompletedScheduleOneCodeIndex", "no_completed_index" },
            { "StaleApiIndex", "no_completed_index" },
            { "ApiIndexUnavailable", "no_completed_index" },
            { "NoVerifiedSceneContainers", "no_completed_index" },
            { "SceneTypeTreeUnavailable", "no_completed_index" },
            { "SceneIndexInProgress", "no_completed_index" },
            { "NoRecoverableSceneObjects", "no_completed_index" },
            { "PartialRecovery", "no_completed_index" },
            { "NoPreferredVerifiedExtraction", "no_completed_index" },
            { "NoReplayVerifiedExtractionInput", "no_completed_index" },
            { "SourceUnavailable", "source_unavailable" },
            { "SourceIntegrityFailure", "source_integrity_failure" },
            { "SceneInputIntegrityFailure", "source_integrity_failure" },
            { "ExtractionIntegrityFailure", "source_integrity_failure" },
            { "SceneSnapshotNotFound", "snapshot_not_found" },
            { "BuildNotFound", "snapshot_not_found" },
            { "NoMatchingEnvironmentSnapshot", "snapshot_not_found" },
            { "CrossBuildCodeIndex", "snapshot_not_found" },
            { "PreferredExtractionChanged", "snapshot_not_found" },
            { "ReplayVerifiedInputChanged", "snapshot_not_found" },
            { "CodeIndexChanged", "snapshot_not_found" },
            { "NoReplayVerifiedInputChanged", "snapshot_not_found" },
            { "IndexBuildMismatch", "snapshot_not_found" },
            { "NoCurrentBuild", "no_current_build" },
            { "AtlasUnavailable", "atlas_unavailable" },
            { "AtlasSchemaBehind", "atlas_unavailable" },
            { "AtlasSchemaAhead", "atlas_unavailable" },
            { "AtlasSchemaUnrecognized", "atlas_unavailable" },
            { "AtlasSchemaUnreadable", "atlas_unavailable" },
            { "UnexpectedToolFailure", "unexpected_tool_failure" },
            { "IncompleteSeamResult", "unexpected_tool_failure" },
            { "InvalidCursor", "invalid_cursor" }
        };

    [Fact]
    public void UnknownCodes_FallBackToSnakeCase()
    {
        Assert.Equal("future_code", McpToolErrorCodes.MapToWireCode("FutureCode"));
    }

    private static async Task<IReadOnlyList<string>> CollectSweepCodesAsync(McpClient client, McpClientTool tool)
    {
        var found = new List<string>();
        Dictionary<string, object?> args;
        try
        {
            args = SweepArguments(tool);
        }
        catch (ArgumentException)
        {
            return found;
        }

        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(tool.Name, args, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            return found;
        }

        if (result.Content.SingleOrDefault() is not TextContentBlock textBlock)
            return found;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(textBlock.Text);
        }
        catch (JsonException)
        {
            return found;
        }

        using (document)
        {
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                found.Add(code.GetString()!);
            }
        }

        return found;
    }

    private static Dictionary<string, object?> SweepArguments(McpClientTool tool)
    {
        var args = new Dictionary<string, object?>();
        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return args;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredSchema) &&
            requiredSchema.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in requiredSchema.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String)
                    required.Add(name.GetString()!);
            }
        }

        foreach (var property in properties.EnumerateObject())
        {
            var types = DeclaredTypes(property.Value);
            if (!required.Contains(property.Name) && !types.Contains("integer") && !types.Contains("number"))
                continue;
            args[property.Name] = SweepValue(property.Name, types);
        }

        return args;
    }

    private static object? SweepValue(string name, HashSet<string> types)
    {
        if (string.Equals(name, "codebase", StringComparison.Ordinal))
            return "scheduleI";

        if (types.Contains("integer") || types.Contains("number"))
            return 0;
        if (types.Contains("boolean"))
            return false;
        if (types.Contains("array"))
            return Array.Empty<object>();
        return "missing-thing";
    }

    private static HashSet<string> DeclaredTypes(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("type", out var type))
            return types;
        if (type.ValueKind == JsonValueKind.String)
            types.Add(type.GetString()!);
        else if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in type.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                    types.Add(entry.GetString()!);
            }
        }

        return types;
    }

    private static async Task<string> CollectCodeAsync(
        McpClient client,
        string tool,
        IReadOnlyDictionary<string, object?> args) =>
        RequireCode(await CallToolAsync(client, tool, args));

    private static async Task<string> CallToolAsync(
        McpClient client,
        string tool,
        IReadOnlyDictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static string RequireCode(string serialized)
    {
        using var document = JsonDocument.Parse(serialized);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static string RequireHint(string serialized)
    {
        using var document = JsonDocument.Parse(serialized);
        return document.RootElement.GetProperty("error").GetProperty("hint").GetString()!;
    }
}
