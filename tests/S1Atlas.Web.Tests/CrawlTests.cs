using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed partial class CrawlTests
{
    [Fact]
    public async Task CrawlFromLandingReachesEveryViewWithoutErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue("/");
        while (queue.Count > 0 && visited.Count < 300)
        {
            var url = queue.Dequeue();
            if (!visited.Add(url))
            {
                continue;
            }

            using var response = await fixture.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(
                response.StatusCode != HttpStatusCode.InternalServerError
                    && (int)response.StatusCode < 500,
                $"Crawled {url} returned {(int)response.StatusCode}.");
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(DerivedLinkPattern().IsMatch(body), $"Crawled {url} wraps a DERIVED statement in a link.");

            foreach (Match match in HrefPattern().Matches(body))
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                if (!href.StartsWith("/", StringComparison.Ordinal)
                    || href.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var hash = href.IndexOf('#');
                if (hash >= 0)
                {
                    href = href[..hash];
                }

                if (href.Length > 1 && !visited.Contains(href))
                {
                    queue.Enqueue(href);
                }
            }
        }

        Assert.Contains("/", visited);
        Assert.Contains(visited, url => url.StartsWith("/search?", StringComparison.Ordinal));
        Assert.Contains(visited, url => url.StartsWith("/symbol/", StringComparison.Ordinal));
        Assert.Contains("/builds", visited);
        Assert.Contains(visited, url =>
            url.StartsWith("/builds/", StringComparison.Ordinal) && url.Length > "/builds/".Length);
        Assert.Contains("/environment", visited);
        Assert.Contains(visited, url => url.StartsWith("/diff", StringComparison.Ordinal));
    }

    // A DERIVED statement is text with a small evidence link at most; the
    // statement itself is never one big link.
    [GeneratedRegex("<p class=\"derived\">[^<]*<a")]
    private static partial Regex DerivedLinkPattern();

    [GeneratedRegex("href=\"(?<href>[^\"]+)\"")]
    private static partial Regex HrefPattern();
}
