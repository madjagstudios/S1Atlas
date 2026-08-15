using S1Atlas.Cli;
using Xunit;

namespace S1Atlas.IntegrationTests.Scene;

public sealed class SceneCliTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-scene-cli-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("scenes", "NoCompletedSceneIndex")]
    [InlineData("scene", "NoCompletedSceneIndex", "scene-a")]
    [InlineData("gameobject", "NoCompletedSceneIndex", "object-a")]
    [InlineData("prefab", "NoCompletedSceneIndex", "prefab-a")]
    [InlineData("component", "NoCompletedSceneIndex", "component-a")]
    public void Scene_query_commands_have_human_and_json_stable_failures(string command, string code, string? selector = null)
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        var humanArgs = selector is null ? [command] : new[] { command, selector };
        var jsonArgs = humanArgs.Append("--json").ToArray();
        using var humanOutput = new StringWriter();
        using var humanError = new StringWriter();
        using var jsonOutput = new StringWriter();
        using var jsonError = new StringWriter();

        var humanExit = application.Invoke(humanArgs, humanOutput, humanError, TestContext.Current.CancellationToken);
        var jsonExit = application.Invoke(jsonArgs, jsonOutput, jsonError, TestContext.Current.CancellationToken);

        Assert.Equal(1, humanExit);
        Assert.Equal(1, jsonExit);
        Assert.Contains(code, humanError.ToString(), StringComparison.Ordinal);
        Assert.Contains(code, jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, jsonError.ToString());
    }

    [Fact]
    public void Index_scene_is_registered_in_human_and_json_modes()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var humanOutput = new StringWriter();
        using var humanError = new StringWriter();
        using var jsonOutput = new StringWriter();
        using var jsonError = new StringWriter();

        var humanExit = application.Invoke(["index", "--scene"], humanOutput, humanError, TestContext.Current.CancellationToken);
        var jsonExit = application.Invoke(["index", "--scene", "--json"], jsonOutput, jsonError, TestContext.Current.CancellationToken);

        Assert.Equal(1, humanExit);
        Assert.Equal(1, jsonExit);
        Assert.Contains("NoEnvironmentSnapshot", humanError.ToString(), StringComparison.Ordinal);
        Assert.Contains("NoEnvironmentSnapshot", jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, jsonError.ToString());
    }

    [Fact]
    public void Scene_query_commands_reject_nonpositive_limits_with_a_machine_stable_code()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = application.Invoke(["scenes", "--limit", "0", "--json"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidLimit", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
