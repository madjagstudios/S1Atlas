using System.Net;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task HeaderNavLinksEveryView()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/builds", cancellationToken);

        Assert.Contains("<nav>", body);
        Assert.Contains("href=\"/\">Home</a>", body);
        Assert.Contains("href=\"/search\">Search</a>", body);
        Assert.Contains("href=\"/builds\">Builds</a>", body);
        Assert.Contains("href=\"/environment\">Environment</a>", body);
        Assert.Contains("href=\"/diff\">Diff</a>", body);
    }

    [Fact]
    public async Task SymbolMemberBreadcrumbLinksNamespaceAndType()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken);
        var breadcrumb = BreadcrumbOf(body);

        Assert.Contains("href=\"/\">Schedule I</a>", breadcrumb);
        Assert.Contains("href=\"/search?q=Demo.", breadcrumb);
        Assert.Contains(">Demo</a>", breadcrumb);
        Assert.Contains($"href=\"/symbol/{SyntheticAtlas.WidgetTypeId}\">Demo.Widget</a>", breadcrumb);
        Assert.Contains(" › ", breadcrumb);
        Assert.Contains("› Run", breadcrumb);
        Assert.DoesNotContain($"href=\"/symbol/{SyntheticAtlas.RunMethodId}\"", breadcrumb);
    }

    [Fact]
    public async Task SymbolBreadcrumbFallsBackToTypeSearch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/symbol/{SyntheticAtlas.AssistMethodId}", cancellationToken);
        var breadcrumb = BreadcrumbOf(body);

        Assert.Contains("kind=type", breadcrumb);
        Assert.Contains(">ServeApi.Helper</a>", breadcrumb);
        Assert.DoesNotContain("href=\"/symbol/", breadcrumb);
    }

    [Fact]
    public async Task SymbolTypePageShowsTypeAsText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/symbol/{SyntheticAtlas.WidgetTypeId}", cancellationToken);
        var breadcrumb = BreadcrumbOf(body);

        Assert.DoesNotContain("href=\"/symbol/", breadcrumb);
        Assert.Contains("› Demo.Widget", breadcrumb);
    }

    [Fact]
    public async Task SearchCoverageUsesNouns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync("/search?q=Widget", cancellationToken);

        Assert.Contains("DERIVED: showing 1&ndash;5 of 5 matches.", body);
    }

    [Fact]
    public async Task SymbolCoverageUsesNouns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/symbol/{SyntheticAtlas.RunMethodId}", cancellationToken);

        Assert.Contains("DERIVED: showing 1 of 1 callers.", body);
        Assert.Contains("DERIVED: showing 5 of 5 references.", body);
    }

    [Fact]
    public async Task DiffPickerRendersBuildForm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync("/diff", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<form", body);
        Assert.Contains("name=\"from\"", body);
        Assert.Contains("name=\"to\"", body);
        Assert.Contains(SyntheticAtlas.BuildIdAValue, body);
        Assert.Contains(SyntheticAtlas.BuildIdBValue, body);
    }

    [Fact]
    public async Task DiffChangesShowSignatures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}",
            cancellationToken);

        Assert.Contains("System.Void Demo.Widget::CheckPhysics()", body);
        Assert.Contains("System.Void Demo.Widget::Turbo()", body);
        Assert.Contains("— → —", body);
    }

    [Fact]
    public async Task DiffWithNoChangesOmitsEmptyList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var body = await fixture.GetStringAsync(
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&kind=field",
            cancellationToken);

        Assert.Contains("Changed symbols (showing 0 of 0).", body);
        Assert.DoesNotContain("<ul></ul>", body);
    }

    private static string BreadcrumbOf(string body)
    {
        const string start = "<nav aria-label=\"Breadcrumb\">";
        var open = body.IndexOf(start, StringComparison.Ordinal);
        Assert.True(open >= 0);
        var close = body.IndexOf("</nav>", open, StringComparison.Ordinal);
        Assert.True(close > open);
        return body.Substring(open, close - open);
    }
}
