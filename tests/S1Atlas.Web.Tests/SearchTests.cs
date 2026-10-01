using System.Net;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task GameSearchListsWidgetMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search?q=Widget", cancellationToken);

        Assert.Contains("FACT: 5 matches in Schedule I (Installed).", body);
        Assert.Contains("DERIVED: showing 1&ndash;5 of 5 matches.", body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.Contains("/symbol/method-serve-run", body);
    }

    [Fact]
    public async Task GameSearchHonorsKindFilter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search?q=Widget&kind=method", cancellationToken);

        Assert.Contains("FACT: 2 matches in Schedule I (Installed).", body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.Contains("Demo.Widget.CheckPhysics", body);
        Assert.DoesNotContain("Demo.WidgetBase", body);
    }

    [Fact]
    public async Task GameSearchPagesThirtyMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var first = await fixture.GetStringAsync("/search?q=PagedType", cancellationToken);
        var second = await fixture.GetStringAsync("/search?q=PagedType&page=1", cancellationToken);

        Assert.Contains("FACT: 30 matches in Schedule I (Installed).", first);
        Assert.Contains("DERIVED: showing 1&ndash;20 of 30 matches.", first);
        Assert.Contains("Paged.PagedType01", first);
        Assert.DoesNotContain("Paged.PagedType30", first);
        Assert.Contains("page=1", first);
        Assert.Contains("DERIVED: showing 21&ndash;30 of 30 matches.", second);
        Assert.Contains("Paged.PagedType30", second);
        Assert.DoesNotContain("Paged.PagedType01", second);
        Assert.Contains("page=0", second);
    }

    [Fact]
    public async Task GameSearchPastTheEndShowsEmptySlice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search?q=PagedType&page=5", cancellationToken);

        Assert.Contains("FACT: 30 matches in Schedule I (Installed).", body);
        Assert.Contains("DERIVED: showing 0 of 30 matches.", body);
        Assert.DoesNotContain("Paged.PagedType", body);
    }

    [Fact]
    public async Task GameSearchWithoutMatchesShowsZero()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search?q=ZzzNoSuchSymbol", cancellationToken);

        Assert.Contains("FACT: 0 matches in Schedule I (Installed).", body);
        Assert.Contains("DERIVED: showing 0 of 0 matches.", body);
    }

    [Fact]
    public async Task SearchWithoutQueryPrompts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search", cancellationToken);

        Assert.Contains("Enter a query to search the index.", body);
        Assert.DoesNotContain("FACT: 0 matches", body);
    }

    [Theory]
    [InlineData("/search?q=Widget&codebase=nope", "Unknown codebase")]
    [InlineData("/search?q=Widget&kind=nope", "Unknown kind")]
    [InlineData("/search?q=Widget&page=-1", "Invalid page")]
    [InlineData("/search?q=Widget&page=abc", "Invalid page")]
    [InlineData("/search?q=Widget&page=25", "Invalid page")]
    [InlineData("/search?q=Widget&page=999999999", "Invalid page")]
    [InlineData("/search?q=Catalog&codebase=s1api&kind=method", "Kind filtering")]
    public async Task SearchRejectsBadQuery(string path, string message)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, body);
    }

    [Fact]
    public async Task ApiSearchListsWidgetMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/search?q=Widget", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, data.GetProperty("returnedCount").GetInt32());
        Assert.Equal("build-serve-1", json.RootElement.GetProperty("build").GetProperty("resolvedBuildId").GetString());
    }

    [Fact]
    public async Task ApiSearchPagesThirtyMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var first = await fixture.GetAsync("/api/search?q=PagedType", cancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(cancellationToken);
        using var second = await fixture.GetAsync("/api/search?q=PagedType&page=1", cancellationToken);
        var secondBody = await second.Content.ReadAsStringAsync(cancellationToken);

        using var firstJson = JsonDocument.Parse(firstBody);
        var firstData = firstJson.RootElement.GetProperty("data");
        Assert.Equal(30, firstData.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, firstData.GetProperty("returnedCount").GetInt32());
        using var secondJson = JsonDocument.Parse(secondBody);
        var secondData = secondJson.RootElement.GetProperty("data");
        Assert.Equal(30, secondData.GetProperty("totalCount").GetInt32());
        Assert.Equal(10, secondData.GetProperty("returnedCount").GetInt32());
        var ids = secondData.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("symbolId").GetString())
            .ToArray();
        Assert.DoesNotContain("type-serve-paged-01", ids);
        Assert.Contains("type-serve-paged-30", ids);
    }

    [Fact]
    public async Task ApiSearchWithoutMatchesIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/search?q=ZzzNoSuchSymbol", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "SymbolNotFound",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiSearchWithoutQueryIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/search", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApiSearchRejectsHugePage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/search?q=Widget&page=999999999", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApiSearchRejectsKindWithApiCodebase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/search?q=Catalog&codebase=s1api&kind=method", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Kind filtering", body);
    }

    [Fact]
    public async Task S1ApiSearchFindsCatalogSymbols()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var page = await fixture.GetStringAsync("/search?q=Catalog&codebase=s1api", cancellationToken);
        using var api = await fixture.GetAsync("/api/search?q=Catalog&codebase=s1api", cancellationToken);
        var apiBody = await api.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("FACT: 2 matches in S1API (Release).", page);
        Assert.Contains("ServeApi.Catalog.Lookup", page);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        using var json = JsonDocument.Parse(apiBody);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "index-serve-api",
            json.RootElement.GetProperty("build").GetProperty("indexId").GetString());
    }

    [Fact]
    public async Task MissingApiScopeReportsNoIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        var page = await fixture.GetStringAsync("/search?q=Catalog&codebase=s1mapi", cancellationToken);
        using var api = await fixture.GetAsync("/api/search?q=Catalog&codebase=s1mapi", cancellationToken);
        var apiBody = await api.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("No completed S1MAPI index exists yet.", page);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        using var json = JsonDocument.Parse(apiBody);
        Assert.Equal("not_found", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "NoCompletedIndex",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SearchOnEmptyRootExplainsNoStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnEmptyRootAsync(cancellationToken);

        var page = await fixture.GetStringAsync("/search?q=Widget", cancellationToken);
        using var api = await fixture.GetAsync("/api/search?q=Widget", cancellationToken);
        var apiBody = await api.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("No Atlas data store was found.", page);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
        using var json = JsonDocument.Parse(apiBody);
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
    }
}
