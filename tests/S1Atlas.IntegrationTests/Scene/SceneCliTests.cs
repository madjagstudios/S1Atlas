using S1Atlas.Cli;
using S1Atlas.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
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

        Assert.True(humanExit == 0, humanOutput.ToString() + humanError); Assert.True(jsonExit == 0, jsonOutput.ToString() + jsonError);
        Assert.Contains("Found 2 scenes. Showing 2.", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(new string('b', 64), humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("sidecar.json", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("scene-a", jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, humanError.ToString()); Assert.Equal(string.Empty, jsonError.ToString());
    }

    [Theory]
    [InlineData("scene", "scene-a", "--children", "--components", "--refs", "reference-a")]
    [InlineData("gameobject", "object-a", "--components", "--refs", "", "component-a")]
    [InlineData("prefab", "prefab-a", "--objects", "--components", "", "Prefab")]
    [InlineData("component", "component-a", "--refs", "--code", "", "Game.Widget")]
    public async Task Selector_commands_render_seeded_successes_in_human_and_json(string command, string selector, string firstOption, string secondOption, string thirdOption, string expected)
    {
        await SeedPublishedSceneAsync();
        var args = new[] { command, selector, firstOption, secondOption, thirdOption }.Where(value => value.Length > 0).ToArray();
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var humanOutput = new StringWriter(); using var humanError = new StringWriter(); using var jsonOutput = new StringWriter(); using var jsonError = new StringWriter();

        var humanExit = application.Invoke(args, humanOutput, humanError, TestContext.Current.CancellationToken);
        var jsonExit = application.Invoke(args.Append("--json").ToArray(), jsonOutput, jsonError, TestContext.Current.CancellationToken);

        Assert.True(humanExit == 0, humanOutput.ToString() + humanError); Assert.True(jsonExit == 0, jsonOutput.ToString() + jsonError);
        Assert.Contains(expected, humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(expected, jsonOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, humanError.ToString()); Assert.Equal(string.Empty, jsonError.ToString());
    }

    [Fact]
    public async Task Ambiguous_scene_renders_candidates_and_verified_container_facts_in_human_and_json()
    {
        await SeedPublishedSceneAsync();
        await InsertAsync("INSERT INTO scenes(scene_id,scene_snapshot_id,container_id,kind,name,source_local_file_id,object_count,root_count,recovery_status) VALUES ('scene-b','snapshot-a','container-a','Scene','Arena',3,1,1,'FullyRecovered');");
        var application = new CliApplication(_dataDirectory, "0.1.0-test"); using var human = new StringWriter(); using var humanError = new StringWriter(); using var json = new StringWriter(); using var jsonError = new StringWriter();

        var humanExit = application.Invoke(["scene", "Arena"], human, humanError, TestContext.Current.CancellationToken);
        var jsonExit = application.Invoke(["scene", "Arena", "--json"], json, jsonError, TestContext.Current.CancellationToken);

        Assert.Equal(1, humanExit); Assert.Equal(1, jsonExit);
        Assert.Contains("scene-a", human.ToString(), StringComparison.Ordinal); Assert.Contains("scene-b", human.ToString(), StringComparison.Ordinal);
        Assert.Contains("Schedule I_Data/level0", human.ToString(), StringComparison.Ordinal); Assert.Contains(new string('b', 64), json.ToString(), StringComparison.Ordinal);
        Assert.Contains("AmbiguousScene", humanError.ToString(), StringComparison.Ordinal); Assert.Contains("AmbiguousScene", json.ToString(), StringComparison.Ordinal);
    }

    private async Task SeedPublishedSceneAsync()
    {
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var environment = new EnvironmentSnapshot(2, new GameBuild("build-a", new string('c', 64), new string('d', 64), DateTimeOffset.UtcNow, true), new InstallationObservation("2022.3.62", "app", "fixture", _dataDirectory, null, null), [], "0.1.0-test", DateTimeOffset.UtcNow);
        await repository.SaveSnapshotAsync(environment, TestContext.Current.CancellationToken);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "atlas.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF; INSERT INTO code_snapshots(snapshot_id,codebase,channel,environment_snapshot_id,source_identity,created_at_utc) VALUES ('code-a','ScheduleI','Installed','environment-build-a','source','2026-08-15'); INSERT INTO index_runs(index_id,snapshot_id,status,started_at_utc,completed_at_utc) VALUES ('index-a','code-a','Completed','2026-08-15','2026-08-15'); INSERT INTO symbols(symbol_id,snapshot_id,canonical_key,kind,qualified_name,signature,is_best_effort) VALUES ('symbol-a','code-a','ScheduleI:Installed:Type:Game.Widget','Type','Game.Widget','Game.Widget',0); INSERT INTO scene_snapshots(scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id, code_index_id, parser_id, parser_version, container_manifest_digest, status, recovery_status, started_at_utc, completed_at_utc, published_at_utc) VALUES ('snapshot-a','build-a','extraction-a','input-a','code-a','index-a','parser','1','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','Completed','FullyRecovered','2026-08-15T00:00:00Z','2026-08-15T00:01:00Z','2026-08-15T00:02:00Z'); INSERT INTO scene_containers(container_id,scene_snapshot_id,relative_path,container_kind,unity_version,serialized_file_version,byte_count,sha256,sidecar_manifest) VALUES ('container-a','snapshot-a','Schedule I_Data/level0','Assets','2022.3.62',22,10,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','sidecar.json'); INSERT INTO scenes(scene_id,scene_snapshot_id,container_id,kind,name,source_local_file_id,object_count,root_count,recovery_status) VALUES ('scene-a','snapshot-a','container-a','Scene','Arena',1,1,1,'FullyRecovered'),('prefab-a','snapshot-a','container-a','Prefab','Prefab',2,1,1,'FullyRecovered'); INSERT INTO game_objects(game_object_id,scene_id,scene_snapshot_id,container_id,local_file_id,name,active,layer,tag,recovery_status) VALUES ('object-a','scene-a','snapshot-a','container-a',11,'Root',1,0,'Untagged','FullyRecovered'); INSERT INTO components(component_id,game_object_id,container_id,local_file_id,unity_class_id,kind,script_assembly,script_namespace,script_class,resolved_type_symbol_id,resolved_code_index_id,type_resolution_status,recovery_status) VALUES ('component-a','object-a','container-a',12,114,'MonoBehaviour','Assembly-CSharp','Game','Widget','symbol-a','index-a','Resolved','FullyRecovered'); INSERT INTO serialized_refs(reference_id,scene_snapshot_id,source_component_id,field_path,declared_type,source_container_id,source_local_file_id,target_container_id,target_local_file_id,target_game_object_id,target_component_id,target_symbol_id,target_text,resolution_status,evidence,recovery_status) VALUES ('reference-a','snapshot-a','component-a','target','GameObject','container-a',12,'container-a',11,'object-a',NULL,'symbol-a',NULL,'Resolved','fixture evidence','FullyRecovered');";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task InsertAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "atlas.db")}"); await connection.OpenAsync(TestContext.Current.CancellationToken); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
