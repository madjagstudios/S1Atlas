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
        Assert.Contains("FACT: 4 members.", body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.Contains("Demo.Widget.Render", body);
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
    public async Task GameMethodPageShowsOverridesAndOverriddenBy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var derived = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.RenderMethodId}", cancellationToken);
        var @base = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.BaseRenderMethodId}", cancellationToken);

        Assert.Contains("<h2>Overrides</h2>", derived);
        Assert.Contains("FACT: 1 overrides in this index.", derived);
        Assert.Contains("Demo.WidgetBase.Render", derived);
        Assert.DoesNotContain("<h2>Derived types</h2>", derived);

        Assert.Contains("<h2>Overridden by</h2>", @base);
        Assert.Contains("FACT: 1 overriders in this index.", @base);
        Assert.Contains("Demo.Widget.Render", @base);
        Assert.DoesNotContain("<h2>Derived types</h2>", @base);
    }

    [Fact]
    public async Task GameTypePageShowsDerivedTypes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.BaseTypeId}", cancellationToken);

        Assert.Contains("<h2>Derived types</h2>", body);
        Assert.Contains("FACT: 1 derived types in this index.", body);
        Assert.Contains("Demo.Widget", body);
        Assert.DoesNotContain("<h2>Overrides</h2>", body);
        Assert.DoesNotContain("<h2>Overridden by</h2>", body);
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

        using var response = await fixture.GetAsync("/symbol/%3Cimg%20src%3Dx%3E", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown symbol", body);
        Assert.DoesNotContain("<img src=x>", body);
        Assert.Contains("&lt;img src=x&gt;", body);
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
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
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
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
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

    [Fact]
    public async Task GameMethodPageShowsDerivedCallersWithRoutesAndSplitTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.RenderMethodId}", cancellationToken);

        Assert.Contains("FACT: 2 callers in this index.", body);
        Assert.Contains("FACT: 1 exact, 1 may-dispatch callers.", body);
        Assert.Contains("DERIVED: may dispatch (via Demo.WidgetBase.Render).", body);
        Assert.Contains("Demo.Service.Execute", body);
        Assert.Contains("Demo.Caller.Invoke", body);
    }

    [Fact]
    public async Task GameMethodPageExactShowsOnlyFactCallers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.RenderMethodId}?exact=1", cancellationToken);

        Assert.Contains("FACT: 1 callers in this index.", body);
        Assert.Contains("FACT: 1 exact, 0 may-dispatch callers.", body);
        Assert.Contains("Demo.Service.Execute", body);
        Assert.DoesNotContain("may dispatch", body);
    }

    [Fact]
    public async Task ApiCallersDefaultsToExpandedAndHonorsExact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var expandedResponse = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RenderMethodId}/callers", cancellationToken);
        var expandedBody = await expandedResponse.Content.ReadAsStringAsync(cancellationToken);
        using var expanded = JsonDocument.Parse(expandedBody);
        var expandedData = expanded.RootElement.GetProperty("data");
        Assert.Equal("resolved", expanded.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, expandedData.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, expandedData.GetProperty("exactCount").GetInt32());
        Assert.Equal(1, expandedData.GetProperty("derivedCount").GetInt32());
        var rows = expandedData.GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.False(rows[0].GetProperty("isDerived").GetBoolean());
        Assert.True(rows[1].GetProperty("isDerived").GetBoolean());
        Assert.Equal(
            "via Demo.WidgetBase.Render",
            Assert.Single(rows[1].GetProperty("routes").EnumerateArray()).GetString());

        using var exactResponse = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RenderMethodId}/callers?exact=1", cancellationToken);
        var exactBody = await exactResponse.Content.ReadAsStringAsync(cancellationToken);
        using var exact = JsonDocument.Parse(exactBody);
        var exactData = exact.RootElement.GetProperty("data");
        Assert.Equal(1, exactData.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, exactData.GetProperty("exactCount").GetInt32());
        Assert.Equal(0, exactData.GetProperty("derivedCount").GetInt32());
        var row = Assert.Single(exactData.GetProperty("relationships").EnumerateArray());
        Assert.False(row.GetProperty("isDerived").GetBoolean());
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    public async Task ApiCallersRejectBadExact(string exact)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers?exact={exact}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid exact", body);
    }

    [Fact]
    public async Task GameMethodPageShowsCreditedCallerDetail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync($"/symbol/{SyntheticAtlas.CreditLeafMethodId}", cancellationToken);

        Assert.Contains("Demo.Credit.Foo", body);
        Assert.Contains("(in lambda)", body);
        Assert.DoesNotContain("b__0_0", body);
    }

    [Fact]
    public async Task GameMethodPageGeneratedShowsRawCaller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/symbol/{SyntheticAtlas.CreditLeafMethodId}?generated=1", cancellationToken);

        Assert.Contains("Demo.Credit.Foo+&lt;&gt;c::&lt;Foo&gt;b__0_0", body);
        Assert.DoesNotContain("(in lambda)", body);
    }

    [Fact]
    public async Task ApiCallersGeneratedReturnsRawRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var creditedResponse = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.CreditLeafMethodId}/callers", cancellationToken);
        var creditedBody = await creditedResponse.Content.ReadAsStringAsync(cancellationToken);
        using var rawResponse = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.CreditLeafMethodId}/callers?generated=1", cancellationToken);
        var rawBody = await rawResponse.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, creditedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);
        using var creditedJson = JsonDocument.Parse(creditedBody);
        using var rawJson = JsonDocument.Parse(rawBody);
        var creditedRow = Assert.Single(
            creditedJson.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray());
        var rawRow = Assert.Single(
            rawJson.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray());
        Assert.Equal(
            "Demo.Credit.Foo",
            creditedRow.GetProperty("source").GetProperty("qualifiedName").GetString());
        Assert.Equal("in lambda", creditedRow.GetProperty("generatedDetail").GetString());
        Assert.Equal(
            "Demo.Credit.Foo+<>c::<Foo>b__0_0",
            rawRow.GetProperty("source").GetProperty("qualifiedName").GetString());
        Assert.False(rawRow.TryGetProperty("generatedDetail", out _));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    public async Task ApiCallersRejectBadGenerated(string generated)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers?generated={generated}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid generated", body);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    public async Task SymbolPageRejectsBadGenerated(string generated)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(
            $"/symbol/{SyntheticAtlas.RunMethodId}?generated={generated}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid generated", body);
    }
}
