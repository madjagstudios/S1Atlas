using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Pins the tool-input contracts: fixed vocabularies are real schema enums
// (invalid values fail binding before tool code runs) and every parameter
// carries a description.
public sealed class McpToolSchemaTests : IClassFixture<SharedHealthyServerFixture>, IClassFixture<SharedScenesServerFixture>
{
    private readonly SharedHealthyServerFixture _fixture;
    private readonly SharedScenesServerFixture _scenes;

    public McpToolSchemaTests(SharedHealthyServerFixture fixture, SharedScenesServerFixture scenes)
    {
        _fixture = fixture;
        _scenes = scenes;
    }

    [Theory]
    [InlineData("search_symbols", "kind", new[] { "Type", "Constructor", "Method", "Field", "Property", "Event" })]
    [InlineData("search_symbols", "scope", new[] { "Game", "Reference", "All" })]
    [InlineData("search_symbols", "codebase", new[] { "scheduleI", "s1api", "s1mapi" })]
    [InlineData("search_symbols", "channel", new[] { "Installed", "Release", "Preview" })]
    [InlineData("find_callers", "scope", new[] { "Game", "Reference", "All" })]
    [InlineData("find_callers", "codebase", new[] { "scheduleI", "s1api", "s1mapi" })]
    [InlineData("find_callers", "channel", new[] { "Installed", "Release", "Preview" })]
    [InlineData("get_type", "codebase", new[] { "scheduleI", "s1api", "s1mapi" })]
    [InlineData("get_type", "channel", new[] { "Installed", "Release", "Preview" })]
    [InlineData("investigate_seam", "scope", new[] { "Game", "Reference", "All" })]
    [InlineData("plan_runtime_proof", "executionBoundary", new[] { "SinglePlayer", "ListenHost", "DedicatedServer", "Client" })]
    public async Task Enum_parameters_advertise_allowed_values(string tool, string parameter, string[] expected)
    {
        var schemas = await McpTestHost.GetToolSchemasAsync(_fixture.Client);

        using var schema = JsonDocument.Parse(schemas[tool]);
        var actual = schema.RootElement
            .GetProperty("properties")
            .GetProperty(parameter)
            .GetProperty("enum")
            .EnumerateArray()
            .Where(element => element.ValueKind != JsonValueKind.Null)
            .Select(element => element.GetString())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal).ToArray(), actual);
    }

    [Theory]
    [InlineData("get_scene", "kind", new[] { "Scene", "Prefab" })]
    [InlineData("list_scenes", "kind", new[] { "Scene", "Prefab" })]
    public async Task Scene_kind_parameters_advertise_allowed_values(string tool, string parameter, string[] expected)
    {
        var schemas = await McpTestHost.GetToolSchemasAsync(_scenes.Client);

        using var schema = JsonDocument.Parse(schemas[tool]);
        var actual = schema.RootElement
            .GetProperty("properties")
            .GetProperty(parameter)
            .GetProperty("enum")
            .EnumerateArray()
            .Where(element => element.ValueKind != JsonValueKind.Null)
            .Select(element => element.GetString())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact]
    public async Task Every_parameter_has_a_description()
    {
        var schemas = await McpTestHost.GetToolSchemasAsync(_fixture.Client);

        Assert.NotEmpty(schemas);
        foreach (var (tool, schemaText) in schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            using var schema = JsonDocument.Parse(schemaText);
            foreach (var property in schema.RootElement.GetProperty("properties").EnumerateObject())
            {
                Assert.True(
                    property.Value.TryGetProperty("description", out var description) &&
                    !string.IsNullOrWhiteSpace(description.GetString()),
                    $"{tool}.{property.Name} has no description");
            }
        }
    }

    [Theory]
    [InlineData("search_symbols", "scope", "bogus")]
    [InlineData("search_symbols", "kind", "bogus")]
    [InlineData("get_type", "codebase", "bogus")]
    [InlineData("get_type", "channel", "bogus")]
    [InlineData("plan_runtime_proof", "executionBoundary", "bogus")]
    public async Task Invalid_enum_values_fail_before_tool_code_runs(string tool, string parameter, string value)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["selector"] = "Demo.Widget",
            ["query"] = "Demo.Widget",
            ["behavioralQuestion"] = "Which authority owns the Demo.Widget run path?",
            ["canonicalIdentity"] = "Demo.Widget.Run",
            ["authority"] = "Demo.Widget",
            ["codebase"] = "scheduleI",
            [parameter] = value
        };

        var outcome = await McpTestHost.CallToolRawAsync(_fixture.Client, tool, arguments);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(outcome.Content)).Text;

        Assert.True(outcome.IsError ?? false);
        Assert.Contains($"argument '{parameter}'", text, StringComparison.Ordinal);
        Assert.Contains("must be one of", text, StringComparison.Ordinal);
        Assert.Contains(value, text, StringComparison.Ordinal);
    }
}
