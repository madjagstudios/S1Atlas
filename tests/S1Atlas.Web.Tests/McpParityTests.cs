using System.Text.Json;
using S1Atlas.Application.Envelope;
using S1Atlas.Mcp;
using S1Atlas.Mcp.Tools;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// The /api/* responses must carry the same envelope status, data, and build
// provenance as the matching MCP tools over the same atlas.
public sealed class McpParityTests
{
    [Fact]
    public async Task GameSearchMatchesMcpStatusTotalsAndResultSets()
    {
        // Serve ranks results (exact name, prefix, substring, bm25) while the
        // MCP tool keeps relevance order, so parity is status, totals, and
        // the result set — not byte order.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        using var served = JsonDocument.Parse(
            await fixture.GetStringAsync("/api/search?q=Widget", cancellationToken));
        var envelope = await tools.SearchSymbolsAsync("Widget", limit: 20, ct: cancellationToken);
        using var expected = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, ToolJsonOptions.Create()));

        Assert.Equal(
            expected.RootElement.GetProperty("status").GetString(),
            served.RootElement.GetProperty("status").GetString());
        var servedData = served.RootElement.GetProperty("data");
        var expectedData = expected.RootElement.GetProperty("data");
        Assert.Equal(
            expectedData.GetProperty("totalCount").GetInt32(),
            servedData.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            expectedData.GetProperty("returnedCount").GetInt32(),
            servedData.GetProperty("returnedCount").GetInt32());
        Assert.Equal(
            expectedData.GetProperty("results").EnumerateArray()
                .Select(result => result.GetProperty("symbolId").GetString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            servedData.GetProperty("results").EnumerateArray()
                .Select(result => result.GetProperty("symbolId").GetString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task ApiSearchMatchesMcpByteForByte()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var tools = new ApiIndexTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        var served = await fixture.GetStringAsync("/api/search?q=Catalog&codebase=s1api", cancellationToken);
        var envelope = await tools.SearchApiSymbolsAsync("s1api", "release", "Catalog", limit: 20, ct: cancellationToken);
        var expected = JsonSerializer.Serialize(envelope, ToolJsonOptions.Create());

        Assert.Equal(expected, served);
    }

    [Fact]
    public async Task BuildListMatchesMcpByteForByte()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        var tools = new BuildEnvironmentTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        var served = await fixture.GetStringAsync("/api/builds", cancellationToken);
        var envelope = await tools.ListBuildsAsync(limit: 500, ct: cancellationToken);
        var expected = JsonSerializer.Serialize(envelope, ToolJsonOptions.Create());

        Assert.Equal(expected, served);
    }

    [Fact]
    public async Task EnvironmentMatchesMcpFacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        var tools = new BuildEnvironmentTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        using var served = JsonDocument.Parse(
            await fixture.GetStringAsync("/api/environment", cancellationToken));
        var envelope = await tools.GetEnvironmentAsync(ct: cancellationToken);
        using var expected = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, ToolJsonOptions.Create()));

        Assert.Equal(
            expected.RootElement.GetProperty("status").GetString(),
            served.RootElement.GetProperty("status").GetString());
        foreach (var property in new[] { "buildId", "executableVersion", "steamAppId", "steamBuildId" })
        {
            Assert.Equal(
                expected.RootElement.GetProperty("data").GetProperty(property).GetString(),
                served.RootElement.GetProperty("data").GetProperty(property).GetString());
        }

        var servedDeps = served.RootElement.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        var expectedDeps = expected.RootElement.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(expectedDeps.Length, servedDeps.Length);
        for (var index = 0; index < expectedDeps.Length; index++)
        {
            Assert.Equal(
                expectedDeps[index].GetProperty("kind").GetString(),
                servedDeps[index].GetProperty("kind").GetString());
            Assert.Equal(
                expectedDeps[index].GetProperty("isInstalled").GetBoolean(),
                servedDeps[index].GetProperty("isInstalled").GetBoolean());
        }
    }

    [Fact]
    public async Task GameSymbolMatchesMcpData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        using var served = JsonDocument.Parse(
            await fixture.GetStringAsync($"/api/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken));
        var envelope = await tools.GetMethodAsync(SyntheticAtlas.RunSelector, ct: cancellationToken);
        using var expected = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, ToolJsonOptions.Create()));

        Assert.Equal(
            expected.RootElement.GetProperty("status").GetString(),
            served.RootElement.GetProperty("status").GetString());
        foreach (var property in new[] { "symbolId", "qualifiedName", "signature" })
        {
            Assert.Equal(
                expected.RootElement.GetProperty("data").GetProperty(property).GetString(),
                served.RootElement.GetProperty("data").GetProperty(property).GetString());
        }

        foreach (var property in new[] { "resolvedBuildId", "indexId" })
        {
            Assert.Equal(
                expected.RootElement.GetProperty("build").GetProperty(property).GetString(),
                served.RootElement.GetProperty("build").GetProperty(property).GetString());
        }
    }

    [Fact]
    public async Task GameCallersMatchMcpData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        using var served = JsonDocument.Parse(
            await fixture.GetStringAsync(
                $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers?limit=50", cancellationToken));
        var envelope = await tools.FindCallersAsync(SyntheticAtlas.RunSelector, limit: 50, ct: cancellationToken);
        using var expected = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, ToolJsonOptions.Create()));

        Assert.Equal("resolved", served.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            expected.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32(),
            served.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            expected.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().First()
                .GetProperty("kind").GetString(),
            served.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().First()
                .GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ApiCallersMatchMcpData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var tools = new ApiIndexTools(McpServerComposition.BuildReadOnlyServices(fixture.Atlas.DataRoot));

        using var served = JsonDocument.Parse(
            await fixture.GetStringAsync(
                $"/api/symbol/{SyntheticAtlas.LookupMethodId}/callers?limit=50", cancellationToken));
        var envelope = await tools.FindApiCallersAsync(
            "s1api", "release", SyntheticAtlas.LookupSelector, limit: 50, ct: cancellationToken);
        using var expected = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, ToolJsonOptions.Create()));

        Assert.Equal("resolved", served.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            expected.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32(),
            served.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, served.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
    }
}
