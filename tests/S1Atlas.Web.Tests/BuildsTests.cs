using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

[Collection("TwoBuildServe")]
public sealed class BuildsTests
{
    private readonly SharedTwoBuildServeFixture _shared;

    public BuildsTests(SharedTwoBuildServeFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task BuildsListShowsNewestFirstWithLabels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/builds", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Indexed and verified", body);
        Assert.DoesNotContain("IndexedVerified", body);
        var newer = body.IndexOf(SyntheticAtlas.BuildIdBValue, StringComparison.Ordinal);
        var older = body.IndexOf(SyntheticAtlas.BuildIdAValue, StringComparison.Ordinal);
        Assert.True(newer >= 0 && older > newer, "Expected newest build first.");
        Assert.Contains($"/builds/{SyntheticAtlas.BuildIdBValue}", body);
        Assert.Contains($"/builds/{SyntheticAtlas.BuildIdAValue}", body);
    }

    [Fact]
    public async Task BuildDetailShowsFactsSurfacesDiffsAndEnvironment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/builds/{SyntheticAtlas.BuildIdBValue}", cancellationToken);

        Assert.Contains($"<h1>{SyntheticAtlas.BuildIdBValue}</h1>", body);
        Assert.Contains("Indexed and verified", body);
        Assert.Contains("2026-08-16", body);
        Assert.Contains($"/search?build={SyntheticAtlas.BuildIdBValue}", body);
        Assert.Contains("50 symbols", body);
        Assert.Contains("/search?codebase=s1api", body);
        Assert.Contains($"/diff?from={SyntheticAtlas.BuildIdAValue}", body);
        Assert.Contains($"to={SyntheticAtlas.BuildIdBValue}", body);
        Assert.Contains("Environment for this build", body);
    }

    [Fact]
    public async Task HistoricalBuildDetailOmitsEnvironmentLink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync(
            $"/builds/{SyntheticAtlas.BuildIdAValue}", cancellationToken);

        Assert.Contains($"<h1>{SyntheticAtlas.BuildIdAValue}</h1>", body);
        Assert.Contains($"/search?build={SyntheticAtlas.BuildIdAValue}", body);
        Assert.Contains("19 symbols", body);
        Assert.Contains($"/diff?from={SyntheticAtlas.BuildIdAValue}", body);
        Assert.Contains($"to={SyntheticAtlas.BuildIdBValue}", body);
        Assert.DoesNotContain("Environment for this build", body);
        Assert.Contains("current build only", body);
    }

    [Fact]
    public async Task UnknownBuildIs404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/builds/no-such-build", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown build", body);
    }

    [Fact]
    public async Task BuildsListOnEmptyRootExplainsNoStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/builds", cancellationToken);

        Assert.Contains("No Atlas data store was found.", body);
    }

    [Fact]
    public async Task ApiBuildsListsNewestFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/builds", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var builds = json.RootElement.GetProperty("data").GetProperty("builds").EnumerateArray().ToArray();
        Assert.Equal(2, builds.Length);
        Assert.Equal(SyntheticAtlas.BuildIdBValue, builds[0].GetProperty("buildId").GetString());
        Assert.Equal(SyntheticAtlas.BuildIdAValue, builds[1].GetProperty("buildId").GetString());
        Assert.True(builds[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(builds[1].GetProperty("isCurrent").GetBoolean());
        Assert.True(builds[0].GetProperty("hasPreferredVerifiedExtraction").GetBoolean());
        Assert.True(builds[0].GetProperty("hasCompletedIndex").GetBoolean());
    }

    [Fact]
    public async Task ApiBuildDetailReportsSurfacesAndDiffs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync(
            $"/api/builds/{SyntheticAtlas.BuildIdBValue}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(SyntheticAtlas.BuildIdBValue, data.GetProperty("buildId").GetString());
        Assert.Equal("Indexed and verified", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("isCurrent").GetBoolean());
        Assert.True(data.GetProperty("environmentAvailable").GetBoolean());
        var surfaces = data.GetProperty("surfaces").EnumerateArray().ToArray();
        Assert.Contains(surfaces, surface =>
            surface.GetProperty("codebase").GetString() == "ScheduleI"
            && surface.GetProperty("symbolCount").GetInt32() == 50);
        var diffs = data.GetProperty("adjacentDiffs").EnumerateArray().ToArray();
        Assert.Single(diffs);
        Assert.Equal(SyntheticAtlas.BuildIdAValue, diffs[0].GetProperty("fromBuildId").GetString());
        Assert.Equal(SyntheticAtlas.BuildIdBValue, diffs[0].GetProperty("toBuildId").GetString());
    }

    [Fact]
    public async Task ApiBuildDetailUnknownIdIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/builds/no-such-build", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "BuildNotFound",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
