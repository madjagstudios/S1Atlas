using System.Net;
using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class SymbolTests
{
    [Fact]
    public async Task GameTypePageShowsMembersSourceAndRelationships()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync($"/symbol/{SyntheticAtlas.WidgetTypeId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<h1>Demo.Widget</h1>", body);
        Assert.Contains("FACT: Type Demo.Widget in Schedule I (Installed).", body);
        Assert.Contains("FACT: 3 members.", body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.Contains("Demo.Widget.CheckPhysics", body);
        Assert.Contains("Demo.Widget._state", body);
        Assert.Contains("Assembly-CSharp.cs", body);
        Assert.Contains("public void Run()", body);
        Assert.Contains("<h2>Callers</h2>", body);
        Assert.Contains("<h2>Callees</h2>", body);
        Assert.Contains("<h2>References</h2>", body);
    }

    [Fact]
    public async Task GameMethodPageShowsCallersAndCallees()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken);

        Assert.Contains("<h1>Demo.Widget.Run</h1>", body);
        Assert.Contains("FACT: 1 callers in this index.", body);
        Assert.Contains("Demo.Caller.Invoke", body);
        Assert.Contains("FACT: 1 callees in this index.", body);
        Assert.Contains("Demo.Service.Execute", body);
        Assert.Contains("public void Run()", body);
    }

    [Fact]
    public async Task ApiTypePageShowsCatalogMember()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.CatalogTypeId}", cancellationToken);

        Assert.Contains("<h1>ServeApi.Catalog</h1>", body);
        Assert.Contains("in S1API (Release).", body);
        Assert.Contains("FACT: 1 members.", body);
        Assert.Contains("ServeApi.Catalog.Lookup", body);
        Assert.DoesNotContain("ServeApi.Helper.Assist", body);
    }

    [Fact]
    public async Task ApiMethodPageShowsApiCaller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.LookupMethodId}", cancellationToken);

        Assert.Contains("FACT: 1 callers in this index.", body);
        Assert.Contains("ServeApi.Helper.Assist", body);
        Assert.Contains("S1Api.cs", body);
    }

    [Fact]
    public async Task HostileSymbolNameIsEscaped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var symbol = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.HostileTypeId}", cancellationToken);
        var search = await fixture.GetStringAsync("/search?q=Evil", cancellationToken);

        Assert.Contains("Evil.&lt;img src=x onerror=alert(1)&gt;", symbol);
        Assert.DoesNotContain("<img src=x", symbol);
        Assert.Contains("Evil.&lt;img src=x onerror=alert(1)&gt;", search);
        Assert.DoesNotContain("<img src=x", search);
    }

    [Fact]
    public async Task UnknownSymbolIs404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/symbol/no-such-symbol", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown symbol", body);
    }

    [Fact]
    public async Task UnknownSymbolIdIsEscapedIn404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/symbol/%3Cb%3Ehi%3C%2Fb%3E", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<b>hi</b>", body);
        Assert.Contains("&lt;b&gt;hi&lt;/b&gt;", body);
    }

    [Fact]
    public async Task SymbolOnEmptyRootExplainsNoStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        using var response = await fixture.GetAsync($"/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No Atlas data store was found.", body);
    }

    [Fact]
    public async Task ApiSymbolReturnsGameSymbol()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync($"/api/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(SyntheticAtlas.RunMethodId, data.GetProperty("symbolId").GetString());
        Assert.Equal("Demo.Widget.Run", data.GetProperty("qualifiedName").GetString());
        Assert.Equal(SyntheticAtlas.RunSelector, data.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task ApiSymbolReturnsApiSymbol()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync($"/api/symbol/{SyntheticAtlas.LookupMethodId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            SyntheticAtlas.LookupMethodId,
            json.RootElement.GetProperty("data").GetProperty("symbolId").GetString());
        Assert.Equal(
            SyntheticAtlas.ApiIndexIdValue,
            json.RootElement.GetProperty("build").GetProperty("indexId").GetString());
    }

    [Fact]
    public async Task ApiSymbolUnknownIdIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/symbol/no-such-symbol", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("notFound", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "SymbolNotFound",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("callers", 1)]
    [InlineData("callees", 1)]
    public async Task ApiGameRelationshipsReportTotals(string direction, int total)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/{direction}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(total, json.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ApiGameReferencesListRelatedRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/references", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("totalCount").GetInt32() > 0, "Expected references.");
        Assert.Contains("ParameterType", body);
    }

    [Fact]
    public async Task ApiRelationshipsUnknownIdIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/symbol/no-such-symbol/callers", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("notFound", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("abc")]
    public async Task ApiRelationshipsRejectBadLimit(string limit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers?limit={limit}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid limit", body);
    }
}
