using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

[Collection("TwoBuildServe")]
public sealed class EnvironmentTests
{
    private readonly SharedTwoBuildServeFixture _shared;

    public EnvironmentTests(SharedTwoBuildServeFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task EnvironmentRedactsPathsButShowsFacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/environment", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("recorded (not shown)", body);
        Assert.Contains("GameAssembly.dll", body);
        Assert.Contains("FakeMod.dll (outside the installation root)", body);
        Assert.Contains("HelperMod.dll (outside the installation root)", body);
        Assert.Contains("mods/LocalMod.dll", body);
        Assert.Contains("assembly-build-serve-b", body);
        Assert.Contains("2022.3", body);
        Assert.Contains("3164500", body);
        Assert.Contains("S1Api", body);
        Assert.Contains("missing", body);
        Assert.DoesNotContain(SyntheticAtlas.LeakRootToken, body);
        Assert.DoesNotContain(SyntheticAtlas.LeakOutsideToken, body);
        Assert.DoesNotContain("servefake-host", body);
    }

    [Fact]
    public async Task EnvironmentOnEmptyRootExplainsNoStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/environment", cancellationToken);

        Assert.Contains("No Atlas data store was found.", body);
    }

    [Fact]
    public async Task ApiEnvironmentRedactsPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/environment", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(SyntheticAtlas.BuildIdBValue, data.GetProperty("buildId").GetString());
        Assert.Equal("recorded (not shown)", data.GetProperty("installationRoot").GetString());
        Assert.Equal("GameAssembly.dll", data.GetProperty("gameAssemblyPath").GetString());
        Assert.Equal("global-metadata.dat", data.GetProperty("globalMetadataPath").GetString());
        var dependencies = data.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(4, dependencies.Length);
        Assert.DoesNotContain(SyntheticAtlas.LeakRootToken, body);
        Assert.DoesNotContain(SyntheticAtlas.LeakOutsideToken, body);
    }

    [Fact]
    public async Task ApiEnvironmentOnEmptyRootIsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/environment", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
    }
}
