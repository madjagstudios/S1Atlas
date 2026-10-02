using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

[Collection("TwoBuildServe")]
public sealed class RankedSearchTests
{
    private readonly SharedTwoBuildServeFixture _shared;

    public RankedSearchTests(SharedTwoBuildServeFixture shared)
    {
        _shared = shared;
    }

    [Fact]
    public async Task RankedSearch_OrdersExactSimpleNameFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/search?q=Widget", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(7, data.GetProperty("totalCount").GetInt32());
        var ids = data.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("symbolId").GetString())
            .ToArray();
        Assert.Equal(SyntheticAtlas.WidgetTypeId, ids[0]);
        Assert.False(data.TryGetProperty("searchNotice", out _));
    }

    [Fact]
    public async Task RankedSearch_MatchesInsideCamelCaseName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync("/search?q=idget", cancellationToken);

        Assert.Contains("Demo.Widget", body);
        Assert.Contains("FACT: 7 matches in Schedule I (Installed).", body);
    }

    [Fact]
    public async Task RankedSearch_TwoCharacterQueryMatchesSimpleNamePrefixOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync("/search?q=Wi", cancellationToken);

        Assert.Contains("FACT: 2 matches in Schedule I (Installed).", body);
        Assert.Contains("Demo.Widget", body);
        Assert.Contains("Demo.WidgetBase", body);
        Assert.DoesNotContain("Paged.PagedType01", body);
        Assert.DoesNotContain("Evil.", body);
    }

    [Fact]
    public async Task RankedSearch_TwoCharacterQueryFindsTypeOutsideNamespacePrefix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync("/search?q=Pa", cancellationToken);

        Assert.Contains("FACT: 31 matches in Schedule I (Installed).", body);
        Assert.Contains("Demo.Payload", body);
    }

    [Fact]
    public async Task RankedSearch_OneCharacterQueryMatchesSimpleNamePrefixOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        var body = await fixture.GetStringAsync("/search?q=R", cancellationToken);

        Assert.Contains("FACT: 4 matches in Schedule I (Installed).", body);
        Assert.Contains("Demo.Result", body);
        Assert.Contains("Demo.Widget.Run", body);
        Assert.Contains("Demo.Widget.Render", body);
        Assert.Contains("Demo.WidgetBase.Render", body);
    }

    [Fact]
    public async Task RankedSearch_HonorsKindFilterWithTrueTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/search?q=Widget&kind=method", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(4, data.GetProperty("totalCount").GetInt32());
        Assert.All(
            data.GetProperty("results").EnumerateArray(),
            result => Assert.Equal("Method", result.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task RankedSearch_PagesWithTrueTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = _shared.Serve;

        using var response = await fixture.GetAsync("/api/search?q=PagedType&page=1", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(30, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(10, data.GetProperty("returnedCount").GetInt32());
    }

    [Fact]
    public async Task UnmigratedAtlas_FallsBackWithVisibleNote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        await DowngradeSearchIndexAsync(fixture.Atlas.DataRoot, cancellationToken);

        using var response = await fixture.GetAsync("/search?q=Widget", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "search index not built; run any s1atlas write command, e.g. `s1atlas index`, to upgrade",
            body);
        Assert.Contains("Demo.Widget", body);
    }

    [Fact]
    public async Task UnmigratedAtlas_ApiCarriesFallbackNotice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        await DowngradeSearchIndexAsync(fixture.Atlas.DataRoot, cancellationToken);

        using var response = await fixture.GetAsync("/api/search?q=Widget", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "search index not built; run any s1atlas write command, e.g. `s1atlas index`, to upgrade",
            json.RootElement.GetProperty("data").GetProperty("searchNotice").GetString());
    }

    private static async Task DowngradeSearchIndexAsync(string dataRoot, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataRoot, "atlas.db")};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS symbols_fts_ai;
            DROP TRIGGER IF EXISTS symbols_fts_ad;
            DROP TRIGGER IF EXISTS symbols_fts_au;
            DROP TABLE IF EXISTS symbols_fts;
            DELETE FROM schema_migrations WHERE version = 16;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
