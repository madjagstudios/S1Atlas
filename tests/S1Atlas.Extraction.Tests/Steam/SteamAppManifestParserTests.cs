using S1Atlas.Extraction.Steam;
using Xunit;

namespace S1Atlas.Extraction.Tests.Steam;

public sealed class SteamAppManifestParserTests
{
    [Fact]
    public void TryParse_ValidManifest_ReturnsDirectAppStateValues()
    {
        const string content = """
            "AppState"
            {
                "appid"      "3164500"
                "Universe"   "1"
                "installdir" "Schedule I"
                "buildid"    "19420567"
                "UserConfig"
                {
                    "language" "english"
                }
            }
            """;

        var parsed = SteamAppManifestParser.TryParse(content, out var manifest);

        Assert.True(parsed);
        Assert.NotNull(manifest);
        Assert.Equal("3164500", manifest.AppId);
        Assert.Equal("Schedule I", manifest.InstallDirectory);
        Assert.Equal("19420567", manifest.BuildId);
    }

    [Fact]
    public void TryParse_NestedDuplicateKey_DoesNotReplaceDirectValue()
    {
        const string content = """
            "AppState"
            {
                "appid"      "3164500"
                "installdir" "Schedule I"
                "buildid"    "19420567"
                "UserConfig"
                {
                    "buildid" "99999999"
                }
            }
            """;

        var parsed = SteamAppManifestParser.TryParse(content, out var manifest);

        Assert.True(parsed);
        Assert.NotNull(manifest);
        Assert.Equal("19420567", manifest.BuildId);
    }

    [Fact]
    public void TryParse_MalformedManifest_ReturnsFalse()
    {
        const string content = """
            "AppState"
            {
                "appid"      "3164500"
                "installdir" "Schedule I"
                "buildid"    "19420567"
            """;

        var parsed = SteamAppManifestParser.TryParse(content, out var manifest);

        Assert.False(parsed);
        Assert.Null(manifest);
    }

    [Fact]
    public void TryParse_EscapedQuotedValue_DecodesValue()
    {
        const string content = """
            "AppState"
            {
                "appid"      "3164500"
                "installdir" "Schedule \"I\"\\Preview"
                "buildid"    "19420567"
            }
            """;

        var parsed = SteamAppManifestParser.TryParse(content, out var manifest);

        Assert.True(parsed);
        Assert.NotNull(manifest);
        Assert.Equal("Schedule \"I\"\\Preview", manifest.InstallDirectory);
    }
}
