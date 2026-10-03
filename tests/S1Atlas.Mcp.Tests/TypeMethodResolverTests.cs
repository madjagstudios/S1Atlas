using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class TypeMethodResolverTests : IClassFixture<SharedHealthyServerFixture>
{
    private const string TypeCanonicalKey = "ScheduleI:Installed:Type:Demo.Widget";
    private const string MethodCanonicalKey = "ScheduleI:Installed:Method:Demo.Widget::Run()";

    private readonly SharedHealthyServerFixture _shared;

    public TypeMethodResolverTests(SharedHealthyServerFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task GetType_ExactNameWithLongerSibling_ResolvesAndMatchesGetSource()
    {
        var atlas = _shared.Atlas;

        var envelopes = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = atlas.TypeSelector },
                ["get_source"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = atlas.TypeSelector }
            });

        using var typeResult = JsonDocument.Parse(envelopes["get_type"]);
        using var sourceResult = JsonDocument.Parse(envelopes["get_source"]);
        Assert.Equal("resolved", StatusOf(typeResult));
        Assert.Equal("resolved", StatusOf(sourceResult));
        Assert.Equal("Demo.Widget", DataOf(typeResult).GetProperty("qualifiedName").GetString());
        var widgetId = DataOf(typeResult).GetProperty("symbolId").GetString()!;
        Assert.Equal(
            sourceResult.RootElement.GetProperty("data").GetProperty("symbol").GetProperty("symbolId").GetString(),
            widgetId);

        var byId = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = widgetId },
                ["get_source"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = widgetId }
            });
        using var byIdType = JsonDocument.Parse(byId["get_type"]);
        using var byIdSource = JsonDocument.Parse(byId["get_source"]);
        Assert.Equal("resolved", StatusOf(byIdType));
        Assert.Equal("resolved", StatusOf(byIdSource));
        Assert.Equal(widgetId, DataOf(byIdType).GetProperty("symbolId").GetString());
        Assert.Equal(
            widgetId,
            byIdSource.RootElement.GetProperty("data").GetProperty("symbol").GetProperty("symbolId").GetString());
    }

    [Fact]
    public async Task GetType_AmbiguousCandidateId_RoundTrips()
    {
        var ambiguous = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = "DealerService" }
            });
        using var ambiguousResult = JsonDocument.Parse(ambiguous["get_type"]);
        Assert.Equal("ambiguous", StatusOf(ambiguousResult));
        var candidateId = ambiguousResult.RootElement.GetProperty("candidates")[0].GetProperty("symbolId").GetString()!;

        var resolved = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = candidateId }
            });
        using var resolvedResult = JsonDocument.Parse(resolved["get_type"]);
        Assert.Equal("resolved", StatusOf(resolvedResult));
        Assert.Equal(candidateId, DataOf(resolvedResult).GetProperty("symbolId").GetString());
    }

    [Fact]
    public async Task GetMethod_AmbiguousCandidateId_RoundTrips()
    {
        var ambiguous = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = "Worker.Run" }
            });
        using var ambiguousResult = JsonDocument.Parse(ambiguous["get_method"]);
        Assert.Equal("ambiguous", StatusOf(ambiguousResult));
        var candidateId = ambiguousResult.RootElement.GetProperty("candidates")[0].GetProperty("symbolId").GetString()!;

        var resolved = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = candidateId }
            });
        using var resolvedResult = JsonDocument.Parse(resolved["get_method"]);
        Assert.Equal("resolved", StatusOf(resolvedResult));
        Assert.Equal(candidateId, DataOf(resolvedResult).GetProperty("symbolId").GetString());
    }

    [Fact]
    public async Task CanonicalKeys_ResolveToExactSymbolsAndMatchGetSource()
    {
        var atlas = _shared.Atlas;

        var envelopes = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = TypeCanonicalKey },
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = MethodCanonicalKey }
            });

        using var typeResult = JsonDocument.Parse(envelopes["get_type"]);
        using var methodResult = JsonDocument.Parse(envelopes["get_method"]);
        Assert.Equal("resolved", StatusOf(typeResult));
        Assert.Equal("resolved", StatusOf(methodResult));
        Assert.Equal("Demo.Widget", DataOf(typeResult).GetProperty("qualifiedName").GetString());
        Assert.Equal(atlas.MethodSelector, DataOf(methodResult).GetProperty("signature").GetString());
        Assert.Equal(
            await GetSourceSymbolIdAsync(_shared.Client, TypeCanonicalKey),
            DataOf(typeResult).GetProperty("symbolId").GetString());
        Assert.Equal(
            await GetSourceSymbolIdAsync(_shared.Client, MethodCanonicalKey),
            DataOf(methodResult).GetProperty("symbolId").GetString());
    }

    [Fact]
    public async Task GetType_WrongKindIdAndKey_ReturnNotFoundNamingKinds()
    {
        var atlas = _shared.Atlas;

        var method = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = atlas.MethodSelector },
                ["get_source"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = atlas.MethodSelector }
            });
        using var methodResult = JsonDocument.Parse(method["get_method"]);
        var methodId = DataOf(methodResult).GetProperty("symbolId").GetString()!;
        using var methodSource = JsonDocument.Parse(method["get_source"]);
        Assert.Equal("resolved", StatusOf(methodSource));
        Assert.Equal(
            methodId,
            methodSource.RootElement.GetProperty("data").GetProperty("symbol").GetProperty("symbolId").GetString());

        var mismatches = await McpTestHost.CallToolsRawAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = methodId },
                ["get_source"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = methodId }
            });
        using var byId = JsonDocument.Parse(TextOf(mismatches["get_type"], expectedIsError: true));
        Assert.Equal("not_found", StatusOf(byId));
        Assert.Equal("SymbolKindMismatch", byId.RootElement.GetProperty("error").GetProperty("code").GetString());
        var byIdMessage = byId.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Method", byIdMessage, StringComparison.Ordinal);
        Assert.Contains("Type", byIdMessage, StringComparison.Ordinal);
        using var byIdSource = JsonDocument.Parse(TextOf(mismatches["get_source"], expectedIsError: false));
        Assert.Equal("resolved", StatusOf(byIdSource));
        Assert.Equal(
            methodId,
            byIdSource.RootElement.GetProperty("data").GetProperty("symbol").GetProperty("symbolId").GetString());

        var byKey = await McpTestHost.CallToolsRawAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = MethodCanonicalKey }
            });
        using var byKeyResult = JsonDocument.Parse(TextOf(byKey["get_type"], expectedIsError: true));
        Assert.Equal("not_found", StatusOf(byKeyResult));
        Assert.Equal("SymbolKindMismatch", byKeyResult.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetMethod_WrongKindIdAndKey_ReturnNotFoundNamingKinds()
    {
        var dealers = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = "DealerService" }
            });
        using var dealersResult = JsonDocument.Parse(dealers["get_type"]);
        var typeId = dealersResult.RootElement.GetProperty("candidates")[0].GetProperty("symbolId").GetString()!;

        var mismatches = await McpTestHost.CallToolsRawAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = typeId }
            });
        using var byId = JsonDocument.Parse(TextOf(mismatches["get_method"], expectedIsError: true));
        Assert.Equal("not_found", StatusOf(byId));
        Assert.Equal("SymbolKindMismatch", byId.RootElement.GetProperty("error").GetProperty("code").GetString());
        var byIdMessage = byId.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Type", byIdMessage, StringComparison.Ordinal);
        Assert.Contains("Method", byIdMessage, StringComparison.Ordinal);

        var byKey = await McpTestHost.CallToolsRawAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = TypeCanonicalKey }
            });
        using var byKeyResult = JsonDocument.Parse(TextOf(byKey["get_method"], expectedIsError: true));
        Assert.Equal("not_found", StatusOf(byKeyResult));
        Assert.Equal("SymbolKindMismatch", byKeyResult.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenuinelyAmbiguousSelectors_RemainAmbiguousWithCandidates()
    {
        var envelopes = await McpTestHost.CallToolsAsync(
            _shared.Client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_type"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = "DealerService" },
                ["get_method"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = "Worker.Run" }
            });

        using var typeResult = JsonDocument.Parse(envelopes["get_type"]);
        Assert.Equal("ambiguous", StatusOf(typeResult));
        Assert.Equal(
            ["Alpha.DealerService", "Beta.DealerService"],
            typeResult.RootElement.GetProperty("candidates").EnumerateArray()
                .Select(candidate => candidate.GetProperty("qualifiedName").GetString()!)
                .Order(StringComparer.Ordinal));

        using var methodResult = JsonDocument.Parse(envelopes["get_method"]);
        Assert.Equal("ambiguous", StatusOf(methodResult));
        Assert.Equal(
            ["Alpha.Worker.Run", "Beta.Worker.Run"],
            methodResult.RootElement.GetProperty("candidates").EnumerateArray()
                .Select(candidate => candidate.GetProperty("qualifiedName").GetString()!)
                .Order(StringComparer.Ordinal));
    }

    private static async Task<string?> GetSourceSymbolIdAsync(McpClient client, string selector)
    {
        var envelopes = await McpTestHost.CallToolsAsync(
            client,
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["get_source"] = new Dictionary<string, object?> { ["codebase"] = "scheduleI", ["selector"] = selector }
            });
        using var result = JsonDocument.Parse(envelopes["get_source"]);
        Assert.Equal("resolved", StatusOf(result));
        return result.RootElement.GetProperty("data").GetProperty("symbol").GetProperty("symbolId").GetString();
    }

    private static string? StatusOf(JsonDocument result) =>
        result.RootElement.GetProperty("status").GetString();

    private static JsonElement DataOf(JsonDocument result) =>
        result.RootElement.GetProperty("data");

    private static string TextOf(CallToolResult result, bool expectedIsError)
    {
        Assert.Equal(expectedIsError, result.IsError ?? false);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
}
