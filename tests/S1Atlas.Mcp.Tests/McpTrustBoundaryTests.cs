using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using S1Atlas.Mcp;
using S1Atlas.Mcp.Tools;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class McpTrustBoundaryTests
    : IClassFixture<SharedHealthyServerFixture>,
        IClassFixture<SharedScenesServerFixture>
{
    private readonly SharedHealthyServerFixture _healthy;
    private readonly SharedScenesServerFixture _scenes;

    public McpTrustBoundaryTests(
        SharedHealthyServerFixture healthy,
        SharedScenesServerFixture scenes)
    {
        _healthy = healthy;
        _scenes = scenes;
    }

    [Fact]
    public async Task StdioHost_UsesProtocolOnlyStdoutAndRegistersEveryReadOnlyTool()
    {
        var tools = await McpTestHost.ListToolsAsync(_scenes.Client);

        Assert.Equal(
            [
                "compare_symbol",
                "find_api_call_sites",
                "find_api_callees",
                "find_api_callers",
                "find_api_field_references",
                "find_api_references",
                "find_api_related_types",
                "find_call_sites",
                "find_callees",
                "find_callers",
                "find_derived_types",
                "find_field_references",
                "find_overriders",
                "find_overrides",
                "find_references",
                "find_related_types",
                "get_api_source",
                "get_callable_surface",
                "get_component",
                "get_environment",
                "get_gameobject",
                "get_method",
                "get_prefab",
                "get_scene",
                "get_scriptable_object",
                "get_source",
                "get_type",
                "investigate_seam",
                "list_api_indexes",
                "list_builds",
                "list_reference_collections",
                "list_scenes",
                "plan_runtime_proof",
                "search_api_symbols",
                "search_symbols"
            ],
            tools.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void McpHost_WiresOnlyStdioAndReadOnlyServices()
    {
        var sources = McpTestHost.ReadHostWiringSources();

        Assert.Contains("WithStdioServerTransport", sources, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyAtlasComposition", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("S1Atlas.Cli", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Diagnostics.Process", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsScheduleOneLocator", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLocator", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("Installer", sources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioHost_CallTool_ReturnsSerializedAuthorityEnvelope()
    {
        var atlas = _scenes.Atlas;

        var serialized = await McpTestHost.CallSearchSymbolsAsync(_scenes.Client);

        using var result = JsonDocument.Parse(serialized);
        var root = result.RootElement;
        Assert.Equal("resolved", root.GetProperty("status").GetString());
        Assert.Equal(atlas.BuildIdB, root.GetProperty("build").GetProperty("resolvedBuildId").GetString());
        Assert.Equal(atlas.IndexIdB, root.GetProperty("build").GetProperty("indexId").GetString());
        Assert.Contains(root.GetProperty("provenance").EnumerateArray(), entry =>
            entry.GetProperty("classification").GetString() == "FACT");
    }

    [Fact]
    public async Task StdioHost_NativeEvidenceLookupIsReadOnlyAndPreservesProvenance()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();
        await atlas.SeedNativeRecoveredEvidenceAsync();
        var before = FileTree.HashAll(atlas.DataRoot);

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns settlement clearing?",
                ["selector"] = atlas.TargetSymbolId,
                ["relationshipLimit"] = 3,
                ["ownerLimit"] = 5,
                ["context"] = 0,
                ["nativeSymbolIds"] = new[] { atlas.NativeSymbolId },
                ["nativeTraversalBudget"] = 25
            });

        var after = FileTree.HashAll(atlas.DataRoot);
        Assert.Equal(before, after);

        using var document = JsonDocument.Parse(serialized);
        var data = document.RootElement.GetProperty("data");
        var native = data.GetProperty("nativeEvidence");
        Assert.Equal("Matched", native.GetProperty("lookupStatus").GetString());
        Assert.Equal("Recovered", native.GetProperty("status").GetString());
        Assert.True(native.GetProperty("isComplete").GetBoolean());
        Assert.Equal("native-tool 1.0.0 (bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb)", native.GetProperty("toolProvenance").GetString());
        Assert.Single(native.GetProperty("directEdges").EnumerateArray());
    }

    [Fact]
    public async Task StdioHost_CodeSymbolSchemasMatchApprovedContractsAndAllowOmittedOptions()
    {
        var atlas = _scenes.Atlas;

        var schemas = await McpTestHost.GetToolSchemasAsync(_scenes.Client);
        AssertSchema(schemas["search_symbols"], ["query", "buildId", "kind", "limit", "scope", "collection"], ["query"]);
        AssertSchema(schemas["list_api_indexes"], ["buildId"], []);
        AssertSchema(schemas["search_api_symbols"], ["codebase", "channel", "query", "limit"], ["codebase", "channel", "query"]);
        AssertSchema(schemas["get_api_source"], ["codebase", "channel", "selector", "context", "relatedLimit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_callers"], ["codebase", "channel", "selector", "limit", "exact"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_callees"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_references"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_related_types"], ["codebase", "channel", "selector", "relationKinds", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_call_sites"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_field_references"], ["codebase", "channel", "selector", "readers", "writers", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(
            schemas["plan_runtime_proof"],
            ["behavioralQuestion", "executionBoundary", "canonicalIdentity", "authority", "knownStaticFacts", "availableObservables", "unavailableObservables", "policyGateSatisfied"],
            ["behavioralQuestion", "executionBoundary", "canonicalIdentity", "authority"]);
        AssertSchema(schemas["list_reference_collections"], [], []);
        AssertSchema(schemas["get_type"], ["selector", "buildId", "limit"], ["selector"]);
        AssertSchema(schemas["get_method"], ["selector", "buildId", "limit"], ["selector"]);
        AssertSchema(schemas["get_source"], ["selector", "buildId", "context", "scope", "collection", "fullType", "relatedLimit"], ["selector"]);
        using var sourceSchema = JsonDocument.Parse(schemas["get_source"]);
        var sourceProperties = sourceSchema.RootElement.GetProperty("properties");
        Assert.False(sourceProperties.GetProperty("fullType").GetProperty("default").GetBoolean());
        Assert.Equal(10, sourceProperties.GetProperty("relatedLimit").GetProperty("default").GetInt32());

        foreach (var (toolName, arguments) in new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["list_api_indexes"] = new Dictionary<string, object?>(),
            ["search_api_symbols"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["query"] = "Missing.Api"
            },
            ["get_api_source"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_callers"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_callees"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_references"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_related_types"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_call_sites"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["find_api_field_references"] = new Dictionary<string, object?>
            {
                ["codebase"] = "s1api",
                ["channel"] = "release",
                ["selector"] = "Missing.Api"
            },
            ["plan_runtime_proof"] = new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which authority owns settlement clearing?",
                ["executionBoundary"] = "singlePlayer",
                ["canonicalIdentity"] = "Game.Seams.Target.Run",
                ["authority"] = "Game.Seams.Target.Run",
                ["availableObservables"] = new[] { "state transition" },
                ["unavailableObservables"] = new[] { "dedicated-server telemetry" },
                ["policyGateSatisfied"] = true
            }
        })
        {
            var apiOutcome = await McpTestHost.CallToolRawAsync(_scenes.Client, toolName, arguments);
            var apiSerialized = Assert.IsType<TextContentBlock>(Assert.Single(apiOutcome.Content)).Text;
            using var apiResult = JsonDocument.Parse(apiSerialized);
            Assert.True(apiResult.RootElement.TryGetProperty("status", out var apiStatus), apiSerialized);
            Assert.Equal(
                apiStatus.GetString() is "not_found" or "invalid" or "unavailable",
                apiOutcome.IsError ?? false);
        }

        AssertSchema(schemas["find_callers"], ["selector", "buildId", "limit", "scope", "collection", "exact"], ["selector"]);
        AssertSchema(schemas["find_callees"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_call_sites"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(
            schemas["find_field_references"],
            ["selector", "buildId", "readers", "writers", "limit", "scope", "collection"],
            ["selector"]);
        AssertSchema(schemas["find_references"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(
            schemas["find_related_types"],
            ["selector", "buildId", "relationKinds", "limit", "scope", "collection"],
            ["selector"]);

        var serialized = await McpTestHost.CallToolAsync(
            _scenes.Client,
            "get_type",
            new Dictionary<string, object?> { ["selector"] = atlas.TypeSelector });
        using var result = JsonDocument.Parse(serialized);
        Assert.Contains(
            result.RootElement.GetProperty("status").GetString(),
            new[] { "resolved", "ambiguous" });

        var sourceSerialized = await McpTestHost.CallToolAsync(
            _scenes.Client,
            "get_source",
            new Dictionary<string, object?>
            {
                ["selector"] = atlas.RuntimeMethodSelector,
                ["context"] = 0,
                ["relatedLimit"] = 0
            });
        using var sourceResult = JsonDocument.Parse(sourceSerialized);
        var sourceData = sourceResult.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, sourceData.GetProperty("runtimeVerification").ValueKind);
        Assert.False(sourceData.TryGetProperty("neighborhood", out _));
        Assert.False(sourceData.TryGetProperty("neighborhoodNotice", out _));

        var neighborhoodSerialized = await McpTestHost.CallToolAsync(
            _scenes.Client,
            "get_source",
            new Dictionary<string, object?>
            {
                ["selector"] = atlas.MethodSelector,
                ["context"] = 0
            });
        using var neighborhoodResult = JsonDocument.Parse(neighborhoodSerialized);
        var neighborhood = neighborhoodResult.RootElement.GetProperty("data").GetProperty("neighborhood");
        Assert.Equal(1, neighborhood.GetProperty("callerTotal").GetInt32());
        Assert.Equal(1, neighborhood.GetProperty("calleeTotal").GetInt32());
        Assert.Empty(neighborhood.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public async Task StdioHost_AllToolSchemasPinRequiredSetsAndOmittedOptionsReturnEnvelopes()
    {
        var atlas = _scenes.Atlas;

        var schemas = await McpTestHost.GetToolSchemasAsync(_scenes.Client);
        Assert.Equal(35, schemas.Count);
        AssertSchema(schemas["compare_symbol"], ["selector", "buildIdA", "buildIdB"], ["selector"]);
        AssertSchema(schemas["find_api_call_sites"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_callees"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_callers"], ["codebase", "channel", "selector", "limit", "exact"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_field_references"], ["codebase", "channel", "selector", "readers", "writers", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_references"], ["codebase", "channel", "selector", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_api_related_types"], ["codebase", "channel", "selector", "relationKinds", "limit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["find_call_sites"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_callees"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_callers"], ["selector", "buildId", "limit", "scope", "collection", "exact"], ["selector"]);
        AssertSchema(schemas["find_derived_types"], ["selector", "buildId", "limit", "depth", "offset", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_field_references"], ["selector", "buildId", "readers", "writers", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_overriders"], ["selector", "buildId", "limit", "depth", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_overrides"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_references"], ["selector", "buildId", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["find_related_types"], ["selector", "buildId", "relationKinds", "limit", "scope", "collection"], ["selector"]);
        AssertSchema(schemas["get_api_source"], ["codebase", "channel", "selector", "context", "relatedLimit"], ["codebase", "channel", "selector"]);
        AssertSchema(schemas["get_callable_surface"], ["selector", "buildId"], ["selector"]);
        AssertSchema(schemas["get_component"], ["selector", "buildId", "sceneSnapshotId", "includeReferences", "includeCode", "limit"], ["selector"]);
        AssertSchema(schemas["get_environment"], ["buildId"], []);
        AssertSchema(schemas["get_gameobject"], ["selector", "buildId", "sceneSnapshotId", "includeChildren", "includeComponents", "includeReferences", "limit"], ["selector"]);
        AssertSchema(schemas["get_method"], ["selector", "buildId", "limit"], ["selector"]);
        AssertSchema(schemas["get_prefab"], ["selector", "buildId", "sceneSnapshotId", "includeObjects", "includeComponents", "includeReferences", "limit"], ["selector"]);
        AssertSchema(schemas["get_scene"], ["selector", "buildId", "sceneSnapshotId", "kind", "includeChildren", "includeComponents", "includeReferences", "limit"], ["selector"]);
        AssertSchema(schemas["get_scriptable_object"], ["selector", "buildId", "sceneSnapshotId"], ["selector"]);
        AssertSchema(schemas["get_source"], ["selector", "buildId", "context", "scope", "collection", "fullType", "relatedLimit"], ["selector"]);
        AssertSchema(schemas["get_type"], ["selector", "buildId", "limit"], ["selector"]);
        AssertSchema(
            schemas["investigate_seam"],
            ["behavioralQuestion", "selector", "buildId", "scope", "collection", "relationshipLimit", "ownerLimit", "context", "details", "nativeSymbolIds", "nativeTraversalBudget"],
            ["behavioralQuestion", "selector"]);
        AssertSchema(schemas["list_api_indexes"], ["buildId"], []);
        AssertSchema(schemas["list_builds"], ["limit"], []);
        AssertSchema(schemas["list_reference_collections"], [], []);
        AssertSchema(schemas["list_scenes"], ["buildId", "sceneSnapshotId", "kind", "query", "limit"], []);
        AssertSchema(
            schemas["plan_runtime_proof"],
            ["behavioralQuestion", "executionBoundary", "canonicalIdentity", "authority", "knownStaticFacts", "availableObservables", "unavailableObservables", "policyGateSatisfied"],
            ["behavioralQuestion", "executionBoundary", "canonicalIdentity", "authority"]);
        AssertSchema(schemas["search_api_symbols"], ["codebase", "channel", "query", "limit"], ["codebase", "channel", "query"]);
        AssertSchema(schemas["search_symbols"], ["query", "buildId", "kind", "limit", "scope", "collection"], ["query"]);

        Dictionary<string, IReadOnlyDictionary<string, object?>> minimalCalls = new()
        {
            ["compare_symbol"] = new Dictionary<string, object?> { ["selector"] = atlas.CompareSelector },
            ["find_api_call_sites"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_api_callees"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_api_callers"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_api_field_references"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_api_references"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_api_related_types"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["find_call_sites"] = new Dictionary<string, object?> { ["selector"] = atlas.EngineCallSiteSelector },
            ["find_callees"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["find_callers"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["find_derived_types"] = new Dictionary<string, object?> { ["selector"] = atlas.HierarchyBaseTypeSelector },
            ["find_field_references"] = new Dictionary<string, object?> { ["selector"] = atlas.GameFieldSelector },
            ["find_overriders"] = new Dictionary<string, object?> { ["selector"] = atlas.HierarchyBaseMethodSelector },
            ["find_overrides"] = new Dictionary<string, object?> { ["selector"] = atlas.HierarchyDerivedMethodSelector },
            ["find_references"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["find_related_types"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["get_api_source"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["selector"] = "Missing.Api" },
            ["get_callable_surface"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["get_component"] = new Dictionary<string, object?> { ["selector"] = atlas.ComponentSelector },
            ["get_environment"] = new Dictionary<string, object?>(),
            ["get_gameobject"] = new Dictionary<string, object?> { ["selector"] = atlas.GameObjectSelector },
            ["get_method"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["get_prefab"] = new Dictionary<string, object?> { ["selector"] = atlas.PrefabSelector },
            ["get_scene"] = new Dictionary<string, object?> { ["selector"] = atlas.SceneNameA },
            ["get_scriptable_object"] = new Dictionary<string, object?> { ["selector"] = atlas.ScriptableAssetSelector },
            ["get_source"] = new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector },
            ["get_type"] = new Dictionary<string, object?> { ["selector"] = atlas.TypeSelector },
            ["investigate_seam"] = new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns the Demo.Widget run path?",
                ["selector"] = atlas.MethodSelector
            },
            ["list_api_indexes"] = new Dictionary<string, object?>(),
            ["list_builds"] = new Dictionary<string, object?>(),
            ["list_reference_collections"] = new Dictionary<string, object?>(),
            ["list_scenes"] = new Dictionary<string, object?>(),
            ["plan_runtime_proof"] = new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which authority owns the Demo.Widget run path?",
                ["executionBoundary"] = "singlePlayer",
                ["canonicalIdentity"] = "Demo.Widget.Run",
                ["authority"] = "Demo.Widget"
            },
            ["search_api_symbols"] = new Dictionary<string, object?> { ["codebase"] = "s1api", ["channel"] = "release", ["query"] = "Missing.Api" },
            ["search_symbols"] = new Dictionary<string, object?> { ["query"] = atlas.KnownSymbolFragment }
        };

        var envelopes = await McpTestHost.CallToolsRawAsync(_scenes.Client, minimalCalls);
        foreach (var (toolName, outcome) in envelopes)
        {
            var serialized = Assert.IsType<TextContentBlock>(Assert.Single(outcome.Content)).Text;
            using var result = JsonDocument.Parse(serialized);
            Assert.True(result.RootElement.TryGetProperty("status", out var status), $"{toolName} did not return a ToolEnvelope: {serialized}");
            Assert.Equal(
                status.GetString() is "not_found" or "invalid" or "unavailable",
                outcome.IsError ?? false);
            if (toolName == "compare_symbol")
            {
                Assert.Equal("invalid", status.GetString());
            }
        }
    }

    [Fact]
    public async Task StdioHost_BindingFailuresNameTheOffendingParameter()
    {
        var atlas = _scenes.Atlas;

        var wrongType = await McpTestHost.CallToolRawAsync(
            _scenes.Client,
            "list_builds",
            new Dictionary<string, object?> { ["limit"] = "not-a-number" });
        Assert.True(wrongType.IsError);
        Assert.Contains("'limit'", Assert.IsType<TextContentBlock>(Assert.Single(wrongType.Content)).Text);

        var wrongItemType = await McpTestHost.CallToolRawAsync(
            _scenes.Client,
            "find_related_types",
            new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector, ["relationKinds"] = new[] { 7 } });
        Assert.True(wrongItemType.IsError);
        Assert.Contains("'relationKinds'", Assert.IsType<TextContentBlock>(Assert.Single(wrongItemType.Content)).Text);

        var missingRequired = await McpTestHost.CallToolRawAsync(
            _scenes.Client,
            "get_type",
            new Dictionary<string, object?>());
        Assert.True(missingRequired.IsError);
        Assert.Contains("'selector'", Assert.IsType<TextContentBlock>(Assert.Single(missingRequired.Content)).Text);
    }

    [Fact]
    public async Task StdioHost_UnrelatedSymbolResultsOmitNullableNestedFields()
    {
        var atlas = _healthy.Atlas;

        var serialized = await McpTestHost.CallToolAsync(
            _healthy.Client,
            "get_method",
            new Dictionary<string, object?> { ["selector"] = atlas.MethodSelector });

        using var document = JsonDocument.Parse(serialized);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        foreach (var propertyName in new[]
        {
            "collection",
            "referenceModId",
            "displayName",
            "version",
            "license",
            "relativePath",
            "sha256"
        })
        {
            Assert.False(data.TryGetProperty(propertyName, out _), $"Unrelated symbol result emitted nullable field {propertyName}.");
        }

        var searchSerialized = await McpTestHost.CallToolAsync(
            _healthy.Client,
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "Dealer",
                ["limit"] = 50
            });
        using var searchDocument = JsonDocument.Parse(searchSerialized);
        var searchSymbols = searchDocument.RootElement.GetProperty("data").GetProperty("results").EnumerateArray();
        Assert.NotEmpty(searchSymbols);
        Assert.All(searchSymbols, searchSymbol =>
        {
            foreach (var propertyName in new[]
            {
                "collection",
                "referenceModId",
                "displayName",
                "version",
                "license",
                "relativePath",
                "sha256"
            })
            {
                Assert.False(searchSymbol.TryGetProperty(propertyName, out _), $"Unrelated nested symbol result emitted nullable field {propertyName}.");
            }
        });

        var buildsSerialized = await McpTestHost.CallToolAsync(
            _healthy.Client,
            "list_builds",
            new Dictionary<string, object?> { ["limit"] = 50 });
        using var buildsDocument = JsonDocument.Parse(buildsSerialized);
        Assert.All(
            buildsDocument.RootElement.GetProperty("provenance").EnumerateArray(),
            provenance =>
            {
                Assert.False(provenance.TryGetProperty("buildId", out _));
                Assert.False(provenance.TryGetProperty("extractionId", out _));
                Assert.False(provenance.TryGetProperty("indexId", out _));
            });
    }

    [Fact]
    public async Task ScopeValidation_PreservesScheduleIDefaultsAndRejectsInvalidCollectionCombinations()
    {
        var atlas = _healthy.Atlas;
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var defaultResult = await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, CancellationToken.None);
        var gameWithCollection = await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, CancellationToken.None, "game", "qol");
        var referenceWithoutCollection = await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, CancellationToken.None, "reference", null);

        Assert.Equal(ToolStatus.Resolved, defaultResult.Status);
        Assert.Equal(atlas.IndexId, defaultResult.Build!.IndexId);
        Assert.Equal(ToolStatus.Invalid, gameWithCollection.Status);
        Assert.Equal("InvalidCollection", gameWithCollection.Error!.Code);
        Assert.Equal(ToolStatus.Invalid, referenceWithoutCollection.Status);
        Assert.Equal("CollectionRequired", referenceWithoutCollection.Error!.Code);
    }

    [Fact]
    public async Task StdioReferenceScopeDoesNotFallThroughForGameOnlySelectors()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        var cases = new Dictionary<string, Dictionary<string, object?>>
        {
            ["get_source"] = new()
            {
                ["selector"] = atlas.MethodSelector,
                ["context"] = 0,
                ["scope"] = "reference",
                ["collection"] = reference.Collection
            },
            ["find_callers"] = new()
            {
                ["selector"] = atlas.MethodSelector,
                ["limit"] = 50,
                ["scope"] = "reference",
                ["collection"] = reference.Collection
            },
            ["find_callees"] = new()
            {
                ["selector"] = atlas.MethodSelector,
                ["limit"] = 50,
                ["scope"] = "reference",
                ["collection"] = reference.Collection
            },
            ["find_references"] = new()
            {
                ["selector"] = atlas.MethodSelector,
                ["limit"] = 50,
                ["scope"] = "reference",
                ["collection"] = reference.Collection
            }
        };

        await using var server = await McpTestServer.StartAsync(
            atlas.DataRoot,
            TestContext.Current.CancellationToken);
        foreach (var (tool, arguments) in cases)
        {
            var outcome = await McpTestHost.CallToolRawAsync(server.Client, tool, arguments);
            var serialized = Assert.IsType<TextContentBlock>(Assert.Single(outcome.Content)).Text;
            using var result = JsonDocument.Parse(serialized);
            var root = result.RootElement;
            var status = root.GetProperty("status").GetString();
            Assert.NotEqual("resolved", status);
            Assert.Equal(
                status is "not_found" or "invalid" or "unavailable",
                outcome.IsError ?? false);
            Assert.DoesNotContain("\"origin\":\"game\"", serialized, StringComparison.Ordinal);
            Assert.True(!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task CallableSurface_RemainsScheduleIOnly()
    {
        var atlas = _healthy.Atlas;
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var result = await tools.GetCallableSurfaceAsync(atlas.MethodSelector, null, CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, result.Status);
        Assert.Equal("ScheduleI", result.Build!.Codebase);
        Assert.Equal("Installed", result.Build.Channel);
    }

    private static void AssertSchema(
        string serializedSchema,
        IReadOnlyList<string> expectedProperties,
        IReadOnlyList<string> expectedRequired)
    {
        using var schema = JsonDocument.Parse(serializedSchema);
        var properties = schema.RootElement.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement.EnumerateObject().Select(property => property.Name)
            : [];
        var required = schema.RootElement.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(value => value.GetString()!)
            : [];
        Assert.Equal(
            expectedProperties.OrderBy(value => value, StringComparer.Ordinal),
            properties
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            expectedRequired.OrderBy(value => value, StringComparer.Ordinal),
            required
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExercisingEveryTool_MutatesNoAtlasFile()
    {
        var atlas = _scenes.Atlas;
        var before = FileTree.HashAll(atlas.DataRoot);

        await McpTestHost.ExerciseEveryToolAsync(atlas);

        var after = FileTree.HashAll(atlas.DataRoot);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task OnlyPreferredIntegrityVerifiedExtractionIsReturned()
    {
        await using var atlas = await McpTestAtlas.SeedPreferredVerifiedBuildWithNonAuthoritativeCandidatesAsync();

        var results = await McpTestHost.QueryEveryCodeToolAsync(atlas);

        Assert.All(results, result =>
        {
            Assert.True(result.Status is ToolStatus.Resolved or ToolStatus.Ambiguous);
            Assert.Equal(atlas.IndexId, result.Build!.IndexId);
            Assert.True(result.Build.IntegrityVerified);
            Assert.NotEmpty(result.AnswerIndexIds);
            Assert.All(result.AnswerIndexIds, indexId => Assert.Equal(atlas.IndexId, indexId));
            Assert.DoesNotContain(result.Provenance, entry =>
                entry.ExtractionId == atlas.NonAuthoritativeExtractionId ||
                entry.IndexId == atlas.NonAuthoritativeIndexId);
        });
    }

    [Fact]
    public async Task CorruptedIndexedSource_ReturnsSourceIntegrityFailure()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        await File.WriteAllTextAsync(atlas.SourcePath, "tampered", CancellationToken.None);

        var envelope = await McpTestHost.GetSourceAsync(atlas);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("SourceIntegrityFailure", envelope.Error!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task ReadOnlyOpen_DoesNotCreateOrMigrate()
    {
        await using var atlas = await McpTestAtlas.CreateAbsentDatabaseRootAsync();

        var envelope = await McpTestHost.SearchSymbolsAsync(atlas.DataRoot, "Dealer");

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("AtlasUnavailable", envelope.Error!.Code);
        Assert.False(File.Exists(Path.Combine(atlas.DataRoot, "atlas.db")));
    }

    [Fact]
    public async Task ReadOnlyOpen_DirectToolsReturnAtlasUnavailableWithoutCreatingDatabase()
    {
        await using var atlas = await McpTestAtlas.CreateAbsentDatabaseRootAsync();

        var (builds, environment, comparison) = await McpTestHost.QueryDirectToolsAgainstAbsentDatabaseAsync(atlas.DataRoot);

        Assert.All([builds, environment, comparison], envelope =>
        {
            Assert.Equal(ToolStatus.Unavailable, envelope.Status);
            Assert.Equal("AtlasUnavailable", envelope.Error!.Code);
        });
        Assert.False(File.Exists(Path.Combine(atlas.DataRoot, "atlas.db")));
    }

    [Fact]
    public async Task CorruptAtlas_ReturnsStableUnavailableWithoutStorageDetails()
    {
        await using var atlas = await McpTestAtlas.CreateCorruptDatabaseRootAsync();

        var envelope = await McpTestHost.SearchSymbolsAsync(atlas.DataRoot, "Dealer");

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("AtlasUnavailable", envelope.Error!.Code);
        Assert.Equal("The Atlas data store is unavailable.", envelope.Error.Message);
        Assert.DoesNotContain(atlas.DataRoot, envelope.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlite", envelope.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonAuthoritativeSceneSnapshot_IsRejectedBeforeQuerying()
    {
        await using var atlas = await McpTestAtlas.SeedPreferredVerifiedBuildWithNonAuthoritativeSceneSnapshotAsync();

        var envelope = await McpTestHost.GetSceneAsync(atlas, atlas.NonAuthoritativeSceneSnapshotId);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("SceneSnapshotNotFound", envelope.Error!.Code);
        Assert.Null(envelope.Data);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
        Assert.DoesNotContain(envelope.Provenance, entry =>
            entry.ExtractionId == atlas.NonAuthoritativeExtractionId ||
            entry.IndexId == atlas.NonAuthoritativeIndexId);
    }

    [Fact]
    public async Task DefaultAndExplicitHistoricalBuildResolve()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();

        var (defaultResult, historicalResult) = await McpTestHost.ResolveDefaultAndHistoricalBuildAsync(atlas);

        Assert.Equal(ToolStatus.Resolved, defaultResult.Status);
        Assert.Equal(atlas.BuildIdB, defaultResult.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdB, defaultResult.Build.IndexId);
        Assert.Equal(ToolStatus.Resolved, historicalResult.Status);
        Assert.Equal(atlas.BuildIdA, historicalResult.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, historicalResult.Build.IndexId);
    }

    [Fact]
    public async Task MissingAmbiguousAndUnavailableQueries_ReturnExplicitStatuses()
    {
        var atlas = _healthy.Atlas;

        var (missing, ambiguous, unavailable) = await McpTestHost.QueryExplicitFailureStatesAsync(atlas);

        Assert.Equal(ToolStatus.NotFound, missing.Status);
        Assert.Equal("SymbolNotFound", missing.Error!.Code);
        Assert.Equal(ToolStatus.Ambiguous, ambiguous.Status);
        Assert.NotEmpty(ambiguous.Candidates);
        Assert.Equal(ToolStatus.Unavailable, unavailable.Status);
        Assert.Equal("NoCurrentBuild", unavailable.Error!.Code);
    }
}

internal static class FileTree
{
    public static IReadOnlyDictionary<string, string> HashAll(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);
}

internal static class McpTestHost
{
    public static async Task<IReadOnlyList<string>> ListToolsThroughStdioAsync(string dataRoot)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await ListToolsAsync(server.Client);
    }

    public static async Task<IReadOnlyList<string>> ListToolsAsync(McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return tools.Select(tool => tool.Name).ToArray();
    }

    public static async Task<IReadOnlyDictionary<string, string>> GetToolSchemasThroughStdioAsync(string dataRoot)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await GetToolSchemasAsync(server.Client);
    }

    public static async Task<IReadOnlyDictionary<string, string>> GetToolSchemasAsync(McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return tools.ToDictionary(
            tool => tool.Name,
            tool => tool.JsonSchema.GetRawText(),
            StringComparer.Ordinal);
    }

    public static async Task<IReadOnlyList<Tool>> ListToolDefinitionsThroughStdioAsync(string dataRoot)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await ListToolDefinitionsAsync(server.Client);
    }

    public static async Task<IReadOnlyList<Tool>> ListToolDefinitionsAsync(McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return tools.Select(tool => tool.ProtocolTool).ToArray();
    }

    public static async Task<string?> GetServerInstructionsThroughStdioAsync(string dataRoot)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return GetServerInstructions(server.Client);
    }

    public static string? GetServerInstructions(McpClient client) => client.ServerInstructions;

    public static string ReadHostWiringSources() =>
        string.Concat(
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "Program.cs")),
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "McpServerComposition.cs")),
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "S1Atlas.Mcp.csproj")));

    public static async Task<string> CallSearchSymbolsThroughStdioAsync(string dataRoot)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await CallSearchSymbolsAsync(server.Client);
    }

    public static async Task<string> CallSearchSymbolsAsync(McpClient client)
    {
        var result = await client.CallToolAsync(
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "Dealer",
                ["buildId"] = null,
                ["kind"] = null,
                ["limit"] = 50
            },
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result.Content));
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    public static async Task<string> CallToolThroughStdioAsync(
        string dataRoot,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await CallToolAsync(server.Client, toolName, arguments);
    }

    public static async Task<string> CallToolAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result.Content));
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    public static async Task<IReadOnlyDictionary<string, string>> CallToolsThroughStdioAsync(
        string dataRoot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> calls)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await CallToolsAsync(server.Client, calls);
    }

    public static async Task<IReadOnlyDictionary<string, string>> CallToolsAsync(
        McpClient client,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> calls)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (toolName, arguments) in calls)
        {
            var result = await client.CallToolAsync(
                toolName,
                arguments,
                cancellationToken: CancellationToken.None);
            Assert.False(result.IsError ?? false, $"{toolName}: {JsonSerializer.Serialize(result.Content)}");
            results[toolName] = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        }

        return results;
    }

    public static async Task<CallToolResult> CallToolRawThroughStdioAsync(
        string dataRoot,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await CallToolRawAsync(server.Client, toolName, arguments);
    }

    public static async Task<CallToolResult> CallToolRawAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments) =>
        await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: CancellationToken.None);

    public static async Task<IReadOnlyDictionary<string, CallToolResult>> CallToolsRawThroughStdioAsync(
        string dataRoot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> calls)
    {
        await using var server = await CreateTrackedServerAsync(dataRoot);
        return await CallToolsRawAsync(server.Client, calls);
    }

    public static async Task<IReadOnlyDictionary<string, CallToolResult>> CallToolsRawAsync(
        McpClient client,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> calls)
    {
        var results = new Dictionary<string, CallToolResult>(StringComparer.Ordinal);
        foreach (var (toolName, arguments) in calls)
        {
            results[toolName] = await client.CallToolAsync(
                toolName,
                arguments,
                cancellationToken: CancellationToken.None);
        }

        return results;
    }

    public static async Task ExerciseEveryToolAsync(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        var code = new CodeSymbolTools(services);
        var compare = new CompareTools(services);
        var build = new BuildEnvironmentTools(services);
        var scene = new SceneTools(services);
        var seam = new SeamTools(services);
        var api = new ApiIndexTools(services);
        var runtimeProof = new RuntimeProofTools();
        var ct = CancellationToken.None;

        await api.ListApiIndexesAsync(ct: ct);
        await api.SearchApiSymbolsAsync("s1api", "release", "Missing.Api", 10, ct);
        await api.GetApiSourceAsync("s1api", "release", "Missing.Api", 0, 0, ct);
        await api.FindApiCallersAsync("s1api", "release", "Missing.Api", 10, ct);
        await api.FindApiCalleesAsync("s1api", "release", "Missing.Api", 10, ct);
        await api.FindApiReferencesAsync("s1api", "release", "Missing.Api", 10, ct);
        await api.FindApiRelatedTypesAsync("s1api", "release", "Missing.Api", null, 10, ct);
        await api.FindApiCallSitesAsync("s1api", "release", "Missing.Api", 10, ct);
        await api.FindApiFieldReferencesAsync("s1api", "release", "Missing.Api", false, false, 10, ct);
        await runtimeProof.PlanRuntimeProofAsync(
            "Which authority owns the Demo.Widget run path?",
            "singlePlayer",
            "Demo.Widget.Run",
            "Demo.Widget",
            ["lifecycle state is persisted"],
            ["state transition"],
            ["dedicated-server telemetry"],
            policyGateSatisfied: true);

        await code.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, ct);
        await code.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, "not-a-kind", 50, ct);
        await code.GetTypeAsync(atlas.TypeSelector, null, ct: ct);
        await code.GetTypeAsync(" ", null, ct: ct);
        await code.GetMethodAsync(atlas.MethodSelector, null, ct: ct);
        await code.GetMethodAsync(" ", null, ct: ct);
        await code.GetSourceAsync(atlas.MethodSelector, null, 0, ct);
        await code.GetSourceAsync(" ", null, 0, ct);
        await code.FindCallersAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindCallersAsync(" ", null, 50, ct);
        await code.FindCalleesAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindCalleesAsync(" ", null, 50, ct);
        await code.FindCallSitesAsync(atlas.EngineCallSiteSelector, null, 50, ct);
        await code.FindCallSitesAsync(" ", null, 50, ct);
        await code.FindFieldReferencesAsync(atlas.GameFieldSelector, null, true, false, 50, ct);
        await code.FindFieldReferencesAsync(atlas.GameFieldSelector, null, false, true, 50, ct);
        await code.FindFieldReferencesAsync(" ", null, false, false, 50, ct);
        await code.FindReferencesAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindReferencesAsync(" ", null, 50, ct);
        await code.FindRelatedTypesAsync(atlas.MethodSelector, null, null, 50, ct);
        await code.FindRelatedTypesAsync(" ", null, null, 50, ct);
        await code.FindOverridesAsync(atlas.HierarchyDerivedMethodSelector, null, 50, ct);
        await code.FindOverridesAsync(" ", null, 50, ct);
        await code.FindOverridersAsync(atlas.HierarchyBaseMethodSelector, null, 50, 10, ct);
        await code.FindOverridersAsync(" ", null, 50, 10, ct);
        await code.FindDerivedTypesAsync(atlas.HierarchyBaseTypeSelector, null, 50, 10, 0, ct);
        await code.FindDerivedTypesAsync(" ", null, 50, 10, 0, ct);
        await new ReferenceCollectionTools(services).ListReferenceCollectionsAsync(ct);

        await compare.CompareSymbolAsync(atlas.CompareSelector, atlas.BuildIdA, atlas.BuildIdB, ct);
        await compare.CompareSymbolAsync(atlas.CompareSelector, atlas.BuildIdA, " ", ct);
        await build.ListBuildsAsync(50, ct);
        await build.ListBuildsAsync(0, ct);
        await build.GetEnvironmentAsync(null, ct);
        await build.GetEnvironmentAsync("missing-build", ct);

        await scene.ListScenesAsync(atlas.BuildIdA, null, null, null, 50, ct);
        await scene.ListScenesAsync(atlas.BuildIdA, null, "not-a-kind", null, 50, ct);
        await scene.GetSceneAsync(atlas.SceneNameA, atlas.BuildIdA, null, null, false, false, false, 50, ct);
        await scene.GetSceneAsync(" ", atlas.BuildIdA, null, null, false, false, false, 50, ct);
        await scene.GetGameObjectAsync(atlas.GameObjectSelector, atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetGameObjectAsync(" ", atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetPrefabAsync(atlas.PrefabSelector, atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetPrefabAsync(" ", atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetComponentAsync(atlas.ComponentSelector, atlas.BuildIdA, null, false, true, 50, ct);
        await scene.GetComponentAsync(" ", atlas.BuildIdA, null, false, true, 50, ct);
        await seam.InvestigateSeamAsync(
            "Which seam owns the Demo.Widget run path?",
            atlas.MethodSelector,
            atlas.BuildIdA,
            null,
            null,
            10,
            5,
            0,
            false,
            ct);
    }

    public static async Task<IReadOnlyList<ToolObservation>> QueryEveryCodeToolAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        var ct = CancellationToken.None;
        var search = await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, ct);
        var type = await tools.GetTypeAsync(atlas.TypeSelector, null, ct: ct);
        var method = await tools.GetMethodAsync(atlas.MethodSelector, null, ct: ct);
        var source = await tools.GetSourceAsync(atlas.MethodSelector, null, 0, ct);
        var callers = await tools.FindCallersAsync(atlas.MethodSelector, null, 50, ct);
        var callSites = await tools.FindCallSitesAsync(atlas.EngineCallSiteSelector, null, 50, ct);
        var fieldReferences = await tools.FindFieldReferencesAsync(atlas.GameFieldSelector, null, false, false, 50, ct);
        var references = await tools.FindReferencesAsync(atlas.MethodSelector, null, 50, ct);
        var relatedTypes = await tools.FindRelatedTypesAsync(atlas.MethodSelector, null, null, 50, ct);
        var overrides = await tools.FindOverridesAsync(atlas.HierarchyDerivedMethodSelector, null, 50, ct);
        var overriders = await tools.FindOverridersAsync(atlas.HierarchyBaseMethodSelector, null, 50, 10, ct);
        var derivedTypes = await tools.FindDerivedTypesAsync(atlas.HierarchyBaseTypeSelector, null, 50, 10, 0, ct);
        return
        [
            Observe(search, search.Data!.Results.Select(result => result.IndexId)),
            Observe(type, SymbolIndexIds(type)),
            Observe(method, SymbolIndexIds(method)),
            Observe(source, [source.Build!.IndexId!]),
            Observe(callers, [callers.Data!.Resolution.Symbol!.IndexId]),
            Observe(callSites, [callSites.Build!.IndexId!]),
            Observe(fieldReferences, [fieldReferences.Data!.Resolution.Symbol!.IndexId]),
            Observe(references, [references.Data!.Resolution.Symbol!.IndexId]),
            Observe(relatedTypes, [relatedTypes.Data!.Resolution.Symbol!.IndexId]),
            Observe(overrides, [overrides.Data!.Resolution.Symbol!.IndexId]),
            Observe(overriders, [overriders.Data!.Resolution.Symbol!.IndexId]),
            Observe(derivedTypes, [derivedTypes.Data!.Resolution.Symbol!.IndexId])
        ];
    }

    public static Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(McpTestAtlas atlas) =>
        new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot))
            .GetSourceAsync(atlas.MethodSelector, null, 0, CancellationToken.None);

    public static Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(string dataRoot, string query) =>
        new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(dataRoot))
            .SearchSymbolsAsync(query, null, null, 50, CancellationToken.None);

    public static async Task<(ToolObservation Builds, ToolObservation Environment, ToolObservation Comparison)> QueryDirectToolsAgainstAbsentDatabaseAsync(string dataRoot)
    {
        var services = McpServerComposition.BuildReadOnlyServices(dataRoot);
        var builds = new BuildEnvironmentTools(services);
        var compare = new CompareTools(services);
        return (
            Observe(await builds.ListBuildsAsync(50, CancellationToken.None)),
            Observe(await builds.GetEnvironmentAsync(null, CancellationToken.None)),
            Observe(await compare.CompareSymbolAsync("N.T.M()", "build-a", "build-b", CancellationToken.None)));
    }

    public static Task<ToolEnvelope<S1Atlas.Indexing.Scene.SceneDocumentQueryResult>> GetSceneAsync(
        McpTestAtlas atlas,
        string sceneSnapshotId) =>
        new SceneTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot))
            .GetSceneAsync(atlas.SceneNameA, atlas.BuildIdA, sceneSnapshotId, null, false, false, false, 50, CancellationToken.None);

    public static async Task<(ToolObservation Default, ToolObservation Historical)> ResolveDefaultAndHistoricalBuildAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        return (
            Observe(await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, CancellationToken.None)),
            Observe(await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, atlas.BuildIdA, null, 50, CancellationToken.None)));
    }

    public static async Task<(ToolObservation Missing, ToolObservation Ambiguous, ToolObservation Unavailable)> QueryExplicitFailureStatesAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        await using var empty = await McpTestAtlas.EmptyAsync();
        var emptyTools = new BuildEnvironmentTools(McpServerComposition.BuildReadOnlyServices(empty.DataRoot));
        return (
            Observe(await tools.GetTypeAsync("Missing.Symbol", null, ct: CancellationToken.None)),
            Observe(await tools.GetTypeAsync("DealerService", null, ct: CancellationToken.None)),
            Observe(await emptyTools.GetEnvironmentAsync(null, CancellationToken.None)));
    }

    private static IReadOnlyList<string> SymbolIndexIds(ToolEnvelope<SymbolQueryResult> envelope) =>
        envelope.Data is not null
            ? [envelope.Data.IndexId]
            : envelope.Candidates.Cast<SymbolQueryResult>().Select(candidate => candidate.IndexId).ToArray();

    private static ToolObservation Observe<T>(ToolEnvelope<T> envelope) where T : class =>
        Observe(envelope, []);

    private static ToolObservation Observe<T>(ToolEnvelope<T> envelope, IEnumerable<string> answerIndexIds) where T : class =>
        new(envelope.Status, envelope.Build, envelope.Candidates, envelope.Provenance, envelope.Error, answerIndexIds.ToArray());

    private static Task<McpTestServer> CreateTrackedServerAsync(string dataRoot) =>
        McpTestServer.StartAsync(dataRoot);

    private static string GetRepoPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }
}

internal sealed record ToolObservation(
    ToolStatus Status,
    BuildContext? Build,
    IReadOnlyList<object> Candidates,
    IReadOnlyList<ProvenanceEntry> Provenance,
    ToolError? Error,
    IReadOnlyList<string> AnswerIndexIds);
