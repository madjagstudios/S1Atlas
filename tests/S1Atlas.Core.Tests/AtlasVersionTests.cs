using System.Text.RegularExpressions;
using S1Atlas.Core.Deployment;
using Xunit;

namespace S1Atlas.Core.Tests;

public sealed class AtlasVersionTests
{
    [Fact]
    public void AssemblyVersionMatchesLatestReleasedChangelog()
    {
        var changelog = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "CHANGELOG.md"));
        var match = Regex.Match(changelog, @"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline);
        Assert.True(match.Success, "No released CHANGELOG version found.");
        var version = AtlasVersion.For(typeof(AtlasVersion).Assembly);
        Assert.Equal(match.Groups[1].Value, version);
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
