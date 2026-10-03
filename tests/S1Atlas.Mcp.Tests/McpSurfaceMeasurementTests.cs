using System.Text;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Records the MCP surface size in bytes so consolidation work can prove the
// tools/list and response shrinkage. Byte counts are written to test output
// (read them from the trx); the facts assert structure only, never numbers.
public sealed class McpSurfaceMeasurementTests : IClassFixture<SharedHealthyServerFixture>, IClassFixture<SharedScenesServerFixture>
{
    private readonly SharedHealthyServerFixture _healthy;
    private readonly SharedScenesServerFixture _scenes;
    private readonly ITestOutputHelper _output;

    public McpSurfaceMeasurementTests(
        SharedHealthyServerFixture healthy,
        SharedScenesServerFixture scenes,
        ITestOutputHelper output)
    {
        _healthy = healthy;
        _scenes = scenes;
        _output = output;
    }

    [Fact]
    public async Task MeasureToolsListBytes()
    {
        var tools = await McpTestHost.ListToolDefinitionsAsync(_healthy.Client);

        Assert.NotEmpty(tools);
        var total = 0;
        foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            total += Utf8Bytes(tool.Name);
            total += Utf8Bytes(tool.Title);
            total += Utf8Bytes(tool.Description);
            total += Utf8Bytes(tool.InputSchema.GetRawText());
        }

        _output.WriteLine($"MEASURE tools/list tools={tools.Count} bytes={total}");
    }

    [Fact]
    public async Task MeasureToolResponseBytes()
    {
        var atlas = _healthy.Atlas;
        var calls = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["search_symbols"] = new Dictionary<string, object?>
            {
                ["codebase"] = "scheduleI",
                ["query"] = atlas.KnownSymbolFragment,
                ["limit"] = 50
            },
            ["get_type"] = new Dictionary<string, object?>
            {
                ["codebase"] = "scheduleI",
                ["selector"] = atlas.TypeSelector,
                ["limit"] = 50
            },
            ["find_callers"] = new Dictionary<string, object?>
            {
                ["codebase"] = "scheduleI",
                ["selector"] = atlas.MethodSelector,
                ["limit"] = 50
            }
        };

        foreach (var (toolName, arguments) in calls)
        {
            var text = await McpTestHost.CallToolAsync(_healthy.Client, toolName, arguments);
            AssertResolved(text);
            _output.WriteLine($"MEASURE response tool={toolName} bytes={Utf8Bytes(text)}");
        }

        var sceneAtlas = _scenes.Atlas;
        var sceneText = await McpTestHost.CallToolAsync(
            _scenes.Client,
            "get_scene",
            new Dictionary<string, object?>
            {
                ["selector"] = sceneAtlas.SceneNameA,
                ["buildId"] = sceneAtlas.BuildIdA,
                ["limit"] = 50
            });
        AssertResolved(sceneText);
        _output.WriteLine($"MEASURE response tool=get_scene bytes={Utf8Bytes(sceneText)}");
    }

    private static int Utf8Bytes(string? value) =>
        value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static void AssertResolved(string envelopeText)
    {
        using var document = JsonDocument.Parse(envelopeText);
        Assert.Equal("resolved", document.RootElement.GetProperty("status").GetString());
    }
}
