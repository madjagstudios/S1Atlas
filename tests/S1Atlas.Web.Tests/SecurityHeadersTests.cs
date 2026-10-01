using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class SecurityHeadersTests
{
    internal const string ExpectedCsp =
        "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    [Fact]
    public async Task HtmlPageCarriesSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync("/builds", cancellationToken);

        AssertSecurityHeaders(response.Headers);
    }

    [Fact]
    public async Task JsonEndpointCarriesSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/builds", cancellationToken);

        AssertSecurityHeaders(response.Headers);
    }

    [Fact]
    public async Task NotFoundCarriesSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        using var response = await fixture.GetAsync("/no-such-page", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertSecurityHeaders(response.Headers);
    }

    [Fact]
    public async Task MisdirectedHostCarriesSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = $"example.com:{fixture.BaseAddress.Port}";

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal((HttpStatusCode)421, response.StatusCode);
        AssertSecurityHeaders(response.Headers);
    }

    [Fact]
    public async Task BoundAddressesSurviveConcurrentReads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var reads = Enumerable.Range(0, 50).Select(_ => Task.Run(
            () =>
            {
                Assert.NotEmpty(fixture.Host.BoundAddresses);
                Assert.StartsWith("http://127.0.0.1:", fixture.Host.BaseAddress.ToString());
            },
            cancellationToken));
        var requests = Enumerable.Range(0, 50).Select(_ => Task.Run(
            async () =>
            {
                using var response = await fixture.GetAsync("/builds", cancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            },
            cancellationToken));
        await Task.WhenAll(reads.Concat(requests));
    }

    private static void AssertSecurityHeaders(HttpResponseHeaders headers)
    {
        Assert.Equal(ExpectedCsp, string.Join("; ", headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", headers.GetValues("Referrer-Policy").Single());
    }
}
