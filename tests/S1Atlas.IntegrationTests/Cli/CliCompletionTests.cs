using System.Text.Json;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CliCompletionTests
{
    [Theory]
    [InlineData("pwsh", "Register-ArgumentCompleter")]
    [InlineData("bash", "complete -F _s1atlas_completions s1atlas")]
    [InlineData("zsh", "compdef _s1atlas s1atlas")]
    public async Task Completion_prints_a_working_registration_script(string shell, string marker)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("completion", shell);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains(marker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[suggest:", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result.StandardOutput);
    }

    [Fact]
    public async Task Completion_rejects_an_unknown_shell()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("completion", "fish");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Shell must be pwsh, bash, or zsh.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Code:    InvalidShell", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suggest_completes_without_validating_the_partial_line()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("[suggest:34]", "s1atlas search x --codebase bogus --");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("--limit", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suggest_lists_matching_commands()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("[suggest:10]", "s1atlas se");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("search", result.StandardOutput, StringComparison.Ordinal);
    }
}
