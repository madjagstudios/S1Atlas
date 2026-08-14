using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using Xunit;

namespace S1Atlas.Indexing.Tests.Upstream;

public sealed class InstalledSourceMatcherTests
{
    [Fact]
    public void Version_only_match_is_not_reported_as_binary_proof()
    {
        var installed = new DependencyVersion(DependencyKind.S1Api, "1.2.3", "S1API.dll", true, "not-reviewed");
        var result = new InstalledSourceMatcher().Match(installed, null, new Dictionary<string, string>(), new Dictionary<string, string> { ["1.2.3"] = new string('a', 40) });

        Assert.Equal(UpstreamMatchKind.SemanticVersion, result.Kind);
        Assert.Contains("not binary proof", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reviewed_binary_hash_outranks_self_reported_embedded_commit()
    {
        var installed = new DependencyVersion(DependencyKind.S1Api, "1.2.3", "S1API.dll", true, "binary-hash");
        var result = new InstalledSourceMatcher().Match(
            installed,
            new string('b', 40),
            new Dictionary<string, string> { ["binary-hash"] = new string('a', 40) },
            new Dictionary<string, string>());

        Assert.Equal(UpstreamMatchKind.ExactBinary, result.Kind);
        Assert.Equal(new string('a', 40), result.CommitSha);
    }

    [Fact]
    public void Reviewed_tag_is_reported_before_unverified_version_match()
    {
        var installed = new DependencyVersion(DependencyKind.S1Api, "1.2.3", "S1API.dll", true);
        var result = new InstalledSourceMatcher().Match(
            installed,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["1.2.3"] = new string('a', 40) },
            new Dictionary<string, string> { ["1.2.3"] = new string('b', 40) });

        Assert.Equal(UpstreamMatchKind.ExactReviewedTag, result.Kind);
        Assert.Equal(new string('b', 40), result.CommitSha);
    }
}
