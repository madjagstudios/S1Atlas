using System.Net;
using System.Net.Http.Json;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using Xunit;

namespace S1Atlas.Indexing.Tests.Upstream;

public sealed class GitHubUpstreamClientTests
{
    [Fact]
    public async Task Rejects_truncated_tree_responses_instead_of_caching_partial_source()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { truncated = true, tree = Array.Empty<object>() })
        }));
        var client = new GitHubUpstreamClient(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(
            new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo"),
            new string('a', 40),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Escapes_each_tree_path_segment_when_fetching_raw_source()
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { truncated = false, tree = new[] { new { type = "blob", path = "src/Foo Bar.cs" } } })
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        var client = new GitHubUpstreamClient(httpClient);

        var snapshot = await client.FetchAsync(
            new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo"),
            new string('a', 40),
            TestContext.Current.CancellationToken);

        Assert.Single(snapshot.Files);
        Assert.Contains(requests, request => request.Host == "raw.githubusercontent.com" && request.AbsolutePath.Contains("Foo%20Bar.cs", StringComparison.Ordinal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
