using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class GameObjectsCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var query = new Option<string?>("--query") { Description = "Case-insensitive name fragment; omit to list every object in scope." }; var scene = new Option<string?>("--scene") { Description = "Optional scene or prefab document: scene ID or exact name." }; var build = new Option<string?>("--build") { Description = "Build ID; omit for current." }; var snapshot = new Option<string?>("--snapshot") { Description = "Optional completed scene snapshot ID for the selected build." };
        var limit = SceneCommandSupport.CreateLimitOption(); var offset = SceneCommandSupport.CreateOffsetOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("game-objects", CliExamples.With("Find indexed game objects by a name fragment, across the snapshot or within one scene.", "s1atlas game-objects --query forklift", "s1atlas game-objects --query truck --scene Main --offset 50"));
        command.Options.Add(query); command.Options.Add(scene); command.Options.Add(build); command.Options.Add(snapshot); command.Options.Add(limit); command.Options.Add(offset); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("game-objects", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (SceneCommandSupport.InvalidPage("game-objects", result.GetValue(json), output, error, result.GetValue(limit), result.GetValue(offset)) is int invalid) return invalid;
            var data = service.GameObjectsAsync(new GameObjectListRequest(result.GetValue(build), result.GetValue(snapshot), result.GetValue(query), result.GetValue(scene), result.GetValue(limit), result.GetValue(offset)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new GameObjectListOutput(data.Status, data.Snapshot, data.Scene, data.SceneCandidates, data.Page.TotalCount, data.Page.ReturnedCount, data.Page.Rows, data.Scenes, data.Containers, SceneCommandSupport.NextOffset(data.HasMore, result.GetValue(offset), result.GetValue(limit)));
            return SceneCommandSupport.Write(new CommandOutput("game-objects", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status, result.GetValue(build), repository, cancellationToken), writer => SceneCommandSupport.WriteGameObjectList(outputData, writer));
        }));
        return command;
    }
}
