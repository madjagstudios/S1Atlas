using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class ToolCapabilityTests : IClassFixture<SharedHealthyServerFixture>
{
    private readonly SharedHealthyServerFixture _shared;

    public ToolCapabilityTests(SharedHealthyServerFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task ToolsList_AdvertisesReadOnlyAnnotationsAndTitleOnEveryTool()
    {
        var tools = await McpTestHost.ListToolDefinitionsAsync(_shared.Client);

        Assert.NotEmpty(tools);
        Assert.All(tools, tool =>
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.Title),
                $"Tool '{tool.Name}' has no title.");
            Assert.True(
                tool.Annotations?.ReadOnlyHint == true,
                $"Tool '{tool.Name}' is not advertised read-only.");
            Assert.True(
                tool.Annotations?.DestructiveHint == false,
                $"Tool '{tool.Name}' is not advertised non-destructive.");
            Assert.True(
                tool.Annotations?.IdempotentHint == true,
                $"Tool '{tool.Name}' is not advertised idempotent.");
            Assert.True(
                tool.Annotations?.OpenWorldHint == false,
                $"Tool '{tool.Name}' is not advertised closed-world.");
        });
    }

    [Fact]
    public async Task CallTool_ResolvedEnvelope_ReturnsIsErrorFalse()
    {
        var result = await McpTestHost.CallToolRawAsync(
            _shared.Client,
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "Dealer",
                ["buildId"] = null,
                ["kind"] = null,
                ["limit"] = 50
            });

        var text = AssertSingleEnvelopeText(result, expectedIsError: false);
        Assert.Equal("resolved", EnvelopeStatus(text));
    }

    [Fact]
    public async Task CallTool_AmbiguousEnvelope_ReturnsIsErrorFalse()
    {
        var result = await McpTestHost.CallToolRawAsync(
            _shared.Client,
            "get_method",
            new Dictionary<string, object?>
            {
                ["selector"] = "worker",
                ["buildId"] = null,
                ["limit"] = 50
            });

        var text = AssertSingleEnvelopeText(result, expectedIsError: false);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("ambiguous", document.RootElement.GetProperty("status").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task CallTool_NotFoundEnvelope_ReturnsIsErrorTrueWithEnvelopeIntact()
    {
        var result = await McpTestHost.CallToolRawAsync(
            _shared.Client,
            "get_type",
            new Dictionary<string, object?>
            {
                ["selector"] = "Demo.DoesNotExist",
                ["buildId"] = null,
                ["limit"] = 50
            });

        var text = AssertSingleEnvelopeText(result, expectedIsError: true);
        Assert.Equal("not_found", EnvelopeStatus(text));
    }

    [Fact]
    public async Task CallTool_InvalidEnvelope_ReturnsIsErrorTrueWithEnvelopeIntact()
    {
        var result = await McpTestHost.CallToolRawAsync(
            _shared.Client,
            "get_type",
            new Dictionary<string, object?>
            {
                ["selector"] = "   ",
                ["buildId"] = null,
                ["limit"] = 50
            });

        var text = AssertSingleEnvelopeText(result, expectedIsError: true);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("invalid", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "InvalidArguments",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CallTool_UnavailableEnvelope_ReturnsIsErrorTrueWithEnvelopeIntact()
    {
        await using var atlas = await McpTestAtlas.EmptyAsync();

        var result = await McpTestHost.CallToolRawThroughStdioAsync(
            atlas.DataRoot,
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "Dealer",
                ["buildId"] = null,
                ["kind"] = null,
                ["limit"] = 50
            });

        var text = AssertSingleEnvelopeText(result, expectedIsError: true);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "NoCurrentBuild",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Initialize_ExposesServerInstructionsWithSelectorSyntax()
    {
        var instructions = McpTestHost.GetServerInstructions(_shared.Client);

        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("canonical key", instructions!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbolId", instructions!, StringComparison.Ordinal);
    }

    private static string AssertSingleEnvelopeText(CallToolResult result, bool expectedIsError)
    {
        Assert.Equal(expectedIsError, result.IsError ?? false);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static string? EnvelopeStatus(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("status").GetString();
    }
}
