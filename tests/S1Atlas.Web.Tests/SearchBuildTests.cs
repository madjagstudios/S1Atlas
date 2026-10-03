using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

[Collection("TwoBuildServe")]
public sealed class SearchBuildTests
{
    private readonly SharedTwoBuildServeFixture _shared;

    public SearchBuildTests(SharedTwoBuildServeFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task BuildScopedSearchFindsHistoricalOnlySymbol()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/search?q=Turbo&build={SyntheticAtlas.BuildIdAValue}", cancellationToken);

        Assert.Contains("FACT: 1 matches in Schedule I (Installed) for build build-serve-a.", body);
        Assert.Contains(SyntheticAtlas.TurboQualifiedName, body);
    }

    [Fact]
    public async Task BuildScopedSearchExcludesCurrentOnlySymbol()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/search?q=CheckPhysics&build={SyntheticAtlas.BuildIdAValue}", cancellationToken);

        Assert.Contains("FACT: 0 matches in Schedule I (Installed) for build build-serve-a.", body);
    }

    [Fact]
    public async Task HistoricalResultsRenderWithoutLinks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/search?q=Turbo&build={SyntheticAtlas.BuildIdAValue}", cancellationToken);

        Assert.DoesNotContain("/symbol/", body);
        Assert.Contains("Symbol pages cover the current build only.", body);
    }

    [Fact]
    public async Task CurrentBuildSearchKeepsLinks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/search?q=Widget&build={SyntheticAtlas.BuildIdBValue}", cancellationToken);

        Assert.Contains("FACT: 13 matches in Schedule I (Installed) for build build-serve-b.", body);
        Assert.Contains($"/symbol/{SyntheticAtlas.RunMethodId}", body);
    }

    [Fact]
    public async Task UnknownBuildScopeExplains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var page = await fixture.GetStringAsync("/search?q=Widget&build=no-such-build", cancellationToken);
        using var api = await fixture.GetAsync("/api/search?q=Widget&build=no-such-build", cancellationToken);
        var apiBody = await api.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("The requested build was not found.", page);
        Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
        using var json = JsonDocument.Parse(apiBody);
        Assert.Equal("invalid", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "snapshot_not_found",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiScopeRejectsBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var page = await fixture.GetAsync(
            $"/search?q=Catalog&codebase=s1api&build={SyntheticAtlas.BuildIdBValue}", cancellationToken);
        var pageBody = await page.Content.ReadAsStringAsync(cancellationToken);
        using var api = await fixture.GetAsync(
            $"/api/search?q=Catalog&codebase=s1api&build={SyntheticAtlas.BuildIdBValue}", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Contains("Build filtering", pageBody);
        Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
    }

    [Fact]
    public async Task ApiBuildScopedSearchFindsHistoricalRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync(
            $"/api/search?q=Turbo&build={SyntheticAtlas.BuildIdAValue}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            SyntheticAtlas.TurboQualifiedName,
            data.GetProperty("results").EnumerateArray().First().GetProperty("qualifiedName").GetString());
        Assert.Equal(
            SyntheticAtlas.BuildIdAValue,
            json.RootElement.GetProperty("build").GetProperty("resolvedBuildId").GetString());
    }
}
