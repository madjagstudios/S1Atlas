using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class DiffTests
{
    [Fact]
    public async Task DiffPageShowsCountsAndLinksCurrentSymbols()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Added", body);
        Assert.Contains("Removed", body);
        Assert.Contains("Paged.PagedType01", body);
        Assert.Contains($"href=\"/symbol/{SyntheticAtlas.CheckPhysicsMethodId}\"", body);
        Assert.Contains(SyntheticAtlas.TurboQualifiedName, body);
        Assert.DoesNotContain("/symbol/a-", body);
    }

    [Fact]
    public async Task DiffPageReversedDirectionUnlinksHistoricalOnlySymbols()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/diff?from={SyntheticAtlas.BuildIdBValue}&to={SyntheticAtlas.BuildIdAValue}",
            cancellationToken);

        Assert.Contains(SyntheticAtlas.TurboQualifiedName, body);
        Assert.DoesNotContain("/symbol/a-", body);
        Assert.Contains($"href=\"/symbol/{SyntheticAtlas.RunMethodId}\"", body);
    }

    [Fact]
    public async Task DiffPageKindFilterNarrowsChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&kind=method",
            cancellationToken);

        Assert.Contains(SyntheticAtlas.TurboQualifiedName, body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.DoesNotContain("Paged.PagedType01", body);
    }

    [Fact]
    public async Task DiffPageSameBuildIsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdAValue}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DiffPageUnknownBuildIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/diff?from=no-such-build&to={SyntheticAtlas.BuildIdBValue}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DiffPageApiCodebaseIsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&codebase=s1api",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DiffPageInvalidKindIsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&kind=bogus",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiDiffReturnsTotalsAndGatedSymbolIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(SyntheticAtlas.BuildIdAValue, data.GetProperty("fromBuildId").GetString());
        Assert.Equal(SyntheticAtlas.BuildIdBValue, data.GetProperty("toBuildId").GetString());
        var totalChanged = data.GetProperty("totalChanged").GetInt32();
        Assert.True(totalChanged > 0);
        var changes = data.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(totalChanged, changes.Length);
        var turbo = changes.Single(change =>
            change.GetProperty("qualifiedName").GetString() == SyntheticAtlas.TurboQualifiedName);
        Assert.Equal("Removed", turbo.GetProperty("classification").GetString());
        Assert.False(turbo.TryGetProperty("symbolId", out _));
        var run = changes.Single(change =>
            change.GetProperty("qualifiedName").GetString() == "Demo.Widget.Run");
        Assert.Equal(SyntheticAtlas.RunMethodId, run.GetProperty("symbolId").GetString());
    }

    [Fact]
    public async Task ApiDiffPageBeyondEndKeepsTrueTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var first = JsonDocument.Parse(await fixture.GetStringAsync(
            $"/api/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&page=0",
            cancellationToken));
        using var beyond = JsonDocument.Parse(await fixture.GetStringAsync(
            $"/api/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&page=99",
            cancellationToken));

        var firstData = first.RootElement.GetProperty("data");
        var beyondData = beyond.RootElement.GetProperty("data");
        Assert.True(firstData.GetProperty("totalChanged").GetInt32() > 0);
        Assert.Equal(
            firstData.GetProperty("totalChanged").GetInt32(),
            beyondData.GetProperty("totalChanged").GetInt32());
        Assert.Equal(99, beyondData.GetProperty("page").GetInt32());
        Assert.Empty(beyondData.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task ApiDiffSameBuildIsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdAValue}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApiDiffUnknownBuildIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/diff?from=no-such-build&to={SyntheticAtlas.BuildIdBValue}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
    }
}
