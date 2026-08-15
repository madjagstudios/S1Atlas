using S1Atlas.Cli;
using S1Atlas.Storage.Sqlite;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task Scenes_success_renders_verified_container_facts_and_counts_in_human_and_json_modes()
    {
        await SeedPublishedSceneAsync();
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var humanOutput = new StringWriter(); using var humanError = new StringWriter();
        using var jsonOutput = new StringWriter(); using var jsonError = new StringWriter();

        var humanExit = application.Invoke(["scenes", "--snapshot", "snapshot-a"], humanOutput, humanError, TestContext.Current.CancellationToken);
        var jsonExit = application.Invoke(["scenes", "--snapshot", "snapshot-a", "--json"], jsonOutput, jsonError, TestContext.Current.CancellationToken);

        Assert.Equal(0, humanExit); Assert.Equal(0, jsonExit);
        Assert.Contains("Found 1 scenes. Showing 1.", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(new string('b', 64), humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("sidecar.json", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("scene-a", jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, humanError.ToString()); Assert.Equal(string.Empty, jsonError.ToString());
    }

    private async Task SeedPublishedSceneAsync()
    {
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "atlas.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF; INSERT INTO scene_snapshots(scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id, code_index_id, parser_id, parser_version, container_manifest_digest, status, recovery_status, started_at_utc, completed_at_utc, published_at_utc) VALUES ('snapshot-a','build-a','extraction-a','input-a','code-a','index-a','parser','1','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','Completed','FullyRecovered','2026-08-15T00:00:00Z','2026-08-15T00:01:00Z','2026-08-15T00:02:00Z'); INSERT INTO scene_containers(container_id,scene_snapshot_id,relative_path,container_kind,unity_version,serialized_file_version,byte_count,sha256,sidecar_manifest) VALUES ('container-a','snapshot-a','Schedule I_Data/level0','Assets','2022.3.62',22,10,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','sidecar.json'); INSERT INTO scenes(scene_id,scene_snapshot_id,container_id,kind,name,source_local_file_id,object_count,root_count,recovery_status) VALUES ('scene-a','snapshot-a','container-a','Scene','Arena',1,1,1,'FullyRecovered');";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
