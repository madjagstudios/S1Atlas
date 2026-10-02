using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class LandingTests
{
    [Fact]
    public async Task LandingShowsResolvedBuildAndIndexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("FACT: resolved build build-serve-1.", body);
        Assert.Contains("Schedule I", body);
        Assert.Contains("FACT: 46 symbols in this index.", body);
        Assert.Contains("S1API", body);
        Assert.Contains("FACT: 3 symbols in this index.", body);
        Assert.Contains("<form", body);
    }

    [Fact]
    public async Task LandingOnEmptyRootExplainsNoStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        using var response = await fixture.GetAsync("/", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No Atlas data store was found.", body);
        Assert.DoesNotContain("FACT: resolved build", body);
    }

    [Fact]
    public async Task ApiStatusReportsIndexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/status", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(
            SyntheticAtlas.BuildIdValue,
            data.GetProperty("resolvedBuildId").GetString());
        Assert.Equal("Resolved", data.GetProperty("authorityStatus").GetString());
        var indexes = data.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Equal("ScheduleI", indexes[0].GetProperty("codebase").GetString());
        Assert.Equal("Installed", indexes[0].GetProperty("channel").GetString());
        Assert.Equal(46, indexes[0].GetProperty("symbolCount").GetInt32());
        Assert.Equal("S1Api", indexes[1].GetProperty("codebase").GetString());
        Assert.Equal("Release", indexes[1].GetProperty("channel").GetString());
        Assert.Equal(3, indexes[1].GetProperty("symbolCount").GetInt32());
    }

    [Fact]
    public async Task ApiStatusOnEmptyRootIsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/status", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "AtlasUnavailable",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
