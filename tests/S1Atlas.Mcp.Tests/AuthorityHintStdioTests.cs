using System.Text.Json;
using ModelContextProtocol.Protocol;
using S1Atlas.Application.Readiness;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Proves every installed-build authority failure carries its fix command
// (or omits the hint when no exact command exists) on the real stdio wire.
public sealed class AuthorityHintStdioTests
{
    [Fact]
    public async Task NoCurrentBuild_NamesScanOverStdio()
    {
        await using var atlas = await McpTestAtlas.EmptyAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget" });

        Assert.Equal("NoCurrentBuild", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Scan, error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task BuildNotFound_NamesBuildsOverStdio()
    {
        await using var atlas = await McpTestAtlas.EmptyAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?>
            {
                ["selector"] = "Demo.Widget",
                ["buildId"] = "missing-build"
            });

        Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Builds, error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AmbiguousBuildPrefix_OmitsHintOverStdio()
    {
        await using var atlas = await McpTestAtlas.SeedAmbiguousBuildsAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?>
            {
                ["selector"] = "Demo.Widget",
                ["buildId"] = "abcdef12"
            });

        Assert.Equal("AmbiguousBuildPrefix", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("hint", out _), error.GetRawText());
    }

    [Fact]
    public async Task NonCurrentBuildWithoutExtraction_TargetsHintAtRequestedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedNonCurrentBuildWithoutExtractionAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?>
            {
                ["selector"] = "Demo.Widget",
                ["buildId"] = McpTestAtlas.NonCurrentBuildIdSeed
            });

        Assert.Equal("NoPreferredVerifiedExtraction", error.GetProperty("code").GetString());
        Assert.Equal(
            "s1atlas extract --build 1234567890ab",
            error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task NoPreferredVerifiedExtraction_NamesExtractOverStdio()
    {
        await using var atlas = await McpTestAtlas.SeedCurrentBuildOnlyAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget" });

        Assert.Equal("NoPreferredVerifiedExtraction", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Extract, error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task ExtractionIntegrityFailure_OmitsHintOverStdio()
    {
        await using var atlas = await McpTestAtlas.SeedCorruptedPreferenceAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget" });

        Assert.Equal("ExtractionIntegrityFailure", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("hint", out _), error.GetRawText());
    }

    [Fact]
    public async Task NoCompletedIndex_NamesIndexOverStdio()
    {
        await using var atlas = await McpTestAtlas.SeedPreferredVerifiedBuildWithoutIndexAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget" });

        Assert.Equal("NoCompletedIndex", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Index, error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task IndexBuildMismatch_OmitsHintOverStdio()
    {
        await using var atlas = await McpTestAtlas.SeedIndexBuildMismatchAsync();

        var error = await CallGetTypeAsync(
            atlas.DataRoot,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget" });

        Assert.Equal("IndexBuildMismatch", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("hint", out _), error.GetRawText());
    }

    private static async Task<JsonElement> CallGetTypeAsync(
        string dataRoot,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var outcome = await McpTestHost.CallToolRawThroughStdioAsync(
            dataRoot, "get_type", WithCodebase(arguments));

        Assert.True(outcome.IsError ?? false);
        var serialized = Assert.IsType<TextContentBlock>(Assert.Single(outcome.Content)).Text;
        using var document = JsonDocument.Parse(serialized);
        return document.RootElement.GetProperty("error").Clone();
    }

    private static IReadOnlyDictionary<string, object?> WithCodebase(
        IReadOnlyDictionary<string, object?> arguments)
    {
        var merged = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        merged["codebase"] = "scheduleI";
        return merged;
    }
}
