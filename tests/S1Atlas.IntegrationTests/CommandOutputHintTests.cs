using System.Text.Json;
using S1Atlas.Cli.Output;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class CommandOutputHintTests
{
    [Fact]
    public void Failure_Human_WritesNextLineAfterCode()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: false, output, error);

        var exitCode = commandOutput.Failure(
            1,
            "NoCurrentBuild",
            "No current environment snapshot is available.",
            hint: "s1atlas scan");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(
            "No current environment snapshot is available." + Environment.NewLine +
            "Code:    NoCurrentBuild" + Environment.NewLine +
            "Next:    s1atlas scan" + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void Failure_Human_OmitsNextLineWhenHintIsNull()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: false, output, error);

        commandOutput.Failure(1, "AmbiguousBuildPrefix", "The build prefix matches.");

        Assert.DoesNotContain("Next:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_Json_WritesHint()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: true, output, error);

        commandOutput.Failure(
            1,
            "NoCompletedIndex",
            "No completed index exists.",
            hint: "s1atlas index");

        using var document = JsonDocument.Parse(output.ToString());
        var errorElement = document.RootElement.GetProperty("error");
        Assert.Equal("NoCompletedIndex", errorElement.GetProperty("code").GetString());
        Assert.Equal("s1atlas index", errorElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void Failure_Json_OmitsHintWhenNull()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: true, output, error);

        commandOutput.Failure(1, "AmbiguousBuildPrefix", "The build prefix matches.");

        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(
            document.RootElement.GetProperty("error").TryGetProperty("hint", out _),
            output.ToString());
    }

    [Fact]
    public void FailureWithData_Human_WritesNextLine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: false, output, error);

        commandOutput.Failure(
            1,
            "NoCompletedIndex",
            "No completed index exists.",
            new { scope = "s1api" },
            hint: "s1atlas index --codebase s1api --channel installed");

        Assert.Contains(
            "Next:    s1atlas index --codebase s1api --channel installed",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FailureWithData_Json_WritesHintAlongsideData()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandOutput = new CommandOutput("search", json: true, output, error);

        commandOutput.Failure(
            1,
            "NoCompletedIndex",
            "No completed index exists.",
            new { scope = "s1api" },
            hint: "s1atlas index --codebase s1api --channel installed");

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("s1api", root.GetProperty("data").GetProperty("scope").GetString());
        Assert.Equal(
            "s1atlas index --codebase s1api --channel installed",
            root.GetProperty("error").GetProperty("hint").GetString());
    }
}
