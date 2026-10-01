using System.Text.RegularExpressions;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// Mirrors the AT-82 portal leak test: serve renders redacted display paths,
// so no view or API response may carry the fixture installation root, any
// drive-letter path, or any UNC prefix.
public sealed partial class ServePathLeakTests
{
    [Fact]
    public async Task ServingNeverEmitsAbsoluteLocalPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        var dataRoot = fixture.Atlas.DataRoot;

        string[] paths =
        [
            "/",
            "/search?q=Widget",
            $"/search?q=Widget&build={SyntheticAtlas.BuildIdBValue}",
            "/search?q=Catalog&codebase=s1api",
            $"/symbol/{SyntheticAtlas.WidgetTypeId}",
            $"/symbol/{SyntheticAtlas.RunMethodId}",
            $"/symbol/{SyntheticAtlas.LookupMethodId}",
            "/builds",
            $"/builds/{SyntheticAtlas.BuildIdAValue}",
            $"/builds/{SyntheticAtlas.BuildIdBValue}",
            "/environment",
            "/diff",
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}",
            $"/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}&kind=type",
            "/api/status",
            "/api/search?q=Widget",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}",
            $"/api/symbol/{SyntheticAtlas.RunMethodId}/callers",
            "/api/builds",
            $"/api/builds/{SyntheticAtlas.BuildIdAValue}",
            "/api/environment",
            $"/api/diff?from={SyntheticAtlas.BuildIdAValue}&to={SyntheticAtlas.BuildIdBValue}",
        ];

        var violations = new List<string>();
        foreach (var path in paths)
        {
            var body = await fixture.GetStringAsync(path, cancellationToken);
            ReportIfPresent(violations, path, body, SyntheticAtlas.LeakInstallationRoot);
            ReportIfPresent(violations, path, body, SyntheticAtlas.LeakRootToken);
            ReportIfPresent(violations, path, body, SyntheticAtlas.LeakOutsideToken);
            ReportIfPresent(violations, path, body, "servefake-host");
            ReportIfPresent(violations, path, body, dataRoot);
            ReportIfPresent(violations, path, body, dataRoot.Replace("\\", "\\\\"));
            ReportMatch(violations, path, body, DriveLetterPath(), "drive-letter path");
            ReportMatch(violations, path, body, UncPrefix(), "UNC prefix");
        }

        Assert.Empty(violations);
    }

    private static void ReportIfPresent(List<string> violations, string path, string body, string fragment)
    {
        var index = body.IndexOf(fragment, StringComparison.Ordinal);
        if (index >= 0)
        {
            violations.Add($"{path}: contains {Describe(fragment)} at offset {index}");
        }
    }

    private static void ReportMatch(List<string> violations, string path, string body, Regex pattern, string label)
    {
        var match = pattern.Match(body);
        if (match.Success)
        {
            violations.Add($"{path}: contains {label} {Describe(match.Value)} at offset {match.Index}");
        }
    }

    private static string Describe(string value) =>
        value.Length <= 80 ? $"'{value}'" : $"'{value[..80]}...'";

    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetterPath();

    [GeneratedRegex(@"\\\\[A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex UncPrefix();
}
