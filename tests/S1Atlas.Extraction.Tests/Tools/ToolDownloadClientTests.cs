using System.Net;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolDownloadClientTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public async Task DownloadAsync_StreamsExactResponseToStaging()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var httpClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
        var destination = Path.Combine(_temporaryDirectory, "tool.bin");
        var client = new ToolDownloadClient(httpClient);

        await client.DownloadAsync(
            new Uri("https://example.test/tool.bin"),
            destination,
            maximumBytes: bytes.Length,
            cancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, cancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_WhenContentLengthExceedsLimit_RejectsBeforeReadingBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var content = new BodyThatMustNotBeReadContent(length: 10);
        using var httpClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        var destination = Path.Combine(_temporaryDirectory, "oversized.bin");
        var client = new ToolDownloadClient(httpClient);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            client.DownloadAsync(
                new Uri("https://example.test/oversized.bin"),
                destination,
                maximumBytes: 5,
                cancellationToken));

        Assert.Equal("ToolSizeMismatch", exception.Code);
        Assert.False(content.WasRead);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_WhenChunkedBodyExceedsLimit_StopsAndDeletesPartialFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var httpClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[9]))
        });
        var destination = Path.Combine(_temporaryDirectory, "chunked.bin");
        var client = new ToolDownloadClient(httpClient);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            client.DownloadAsync(
                new Uri("https://example.test/chunked.bin"),
                destination,
                maximumBytes: 8,
                cancellationToken));

        Assert.Equal("ToolSizeMismatch", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_WhenStatusIsNotSuccess_ReportsToolDownloadFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var httpClient = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));
        var destination = Path.Combine(_temporaryDirectory, "failed.bin");
        var client = new ToolDownloadClient(httpClient);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            client.DownloadAsync(
                new Uri("https://example.test/failed.bin"),
                destination,
                maximumBytes: 100,
                cancellationToken));

        Assert.Equal("ToolDownloadFailed", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_WhenFinalRequestUriIsNotHttps_RejectsBeforeReadingBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var content = new BodyThatMustNotBeReadContent(length: 4);
        using var httpClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://example.test/tool.bin"),
            Content = content
        });
        var destination = Path.Combine(_temporaryDirectory, "redirected.bin");
        var client = new ToolDownloadClient(httpClient);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            client.DownloadAsync(
                new Uri("https://example.test/tool.bin"),
                destination,
                maximumBytes: 10,
                cancellationToken));

        Assert.Equal("ToolDownloadFailed", exception.Code);
        Assert.False(content.WasRead);
        Assert.False(File.Exists(destination));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(new StubHttpMessageHandler(responseFactory));

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class BodyThatMustNotBeReadContent(long length) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("The response body was read unexpectedly.");
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasRead = true;
            throw new InvalidOperationException("The response body was read unexpectedly.");
        }
    }
}
