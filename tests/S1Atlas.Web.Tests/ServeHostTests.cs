using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class ServeHostTests
{
    [Fact]
    public async Task ServesOnlyLoopbackAddresses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        Assert.NotEmpty(fixture.Host.BoundAddresses);
        foreach (var address in fixture.Host.BoundAddresses)
        {
            var host = new Uri(address).Host.Trim('[', ']');
            Assert.True(
                IPAddress.TryParse(host, out var parsed) && IPAddress.IsLoopback(parsed),
                $"Bound address '{address}' is not loopback.");
        }
    }

    [Fact]
    public async Task BaseAddressPrefersIpv4Loopback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        Assert.StartsWith("http://127.0.0.1:", fixture.BaseAddress.ToString());
    }

    [Fact]
    public async Task AnswersLocalhostHostHeader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = $"localhost:{fixture.BaseAddress.Port}";

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("evil.example")]
    [InlineData("192.168.1.10")]
    public async Task RejectsNonLoopbackHostHeader(string host)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = $"{host}:{fixture.BaseAddress.Port}";

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }

    [Fact]
    public async Task RejectsWrongPortHostHeader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "127.0.0.1:1";

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }

    [Fact]
    public async Task RejectsRequestWithoutHostHeader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, fixture.BaseAddress.Port, cancellationToken);
        await using var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);

        var body = await ReadAllAsync(stream, cancellationToken);

        var statusLine = body.Split("\r\n", StringSplitOptions.None)[0];
        Assert.Contains("400", statusLine, StringComparison.Ordinal);
        Assert.Contains("Requests must carry a Host header.", body);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task RejectsNonGetMethods(string method)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/");

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, HEAD", string.Join(", ", response.Content.Headers.Allow));
    }

    [Fact]
    public async Task HeadMirrorsGetStatusWithoutBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var head = new HttpRequestMessage(HttpMethod.Head, "/");
        using var headResponse = await fixture.Client.SendAsync(head, cancellationToken);
        using var getResponse = await fixture.GetAsync("/", cancellationToken);

        Assert.Equal(getResponse.StatusCode, headResponse.StatusCode);
        Assert.Equal(
            getResponse.Content.Headers.ContentType?.ToString(),
            headResponse.Content.Headers.ContentType?.ToString());
        Assert.Equal(string.Empty, await headResponse.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task SendsNoCorsHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/status", cancellationToken);

        Assert.False(response.Headers.Contains("access-control-allow-origin"), "CORS header present.");
        Assert.False(response.Headers.Contains("access-control-allow-methods"), "CORS header present.");
        Assert.Empty(response.Headers.Server);
    }

    [Fact]
    public async Task UnknownPageIsHtml404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/no-such-page", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("Not found", body);
    }

    [Fact]
    public async Task UnknownApiEndpointIsJson404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);

        using var response = await fixture.GetAsync("/api/no-such-endpoint", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid_arguments", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void CreateRejectsEmptyDataRoot()
    {
        Assert.Throws<ArgumentException>(() => ServeHost.Create(new ServeOptions("", 0)));
    }

    [Fact]
    public void CreateRejectsOutOfRangePort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ServeHost.Create(new ServeOptions(Path.GetTempPath(), -1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ServeHost.Create(new ServeOptions(Path.GetTempPath(), 70000)));
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
