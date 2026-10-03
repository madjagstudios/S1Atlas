using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class GameObjectCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("game-object-id|scene-id/name") { Description = "Game object ID or scene-id/name." }; var children = new Option<bool>("--children") { Description = "Include child game objects." }; var components = new Option<bool>("--components") { Description = "Include components." }; var refs = new Option<bool>("--refs") { Description = "Include references." }; var limit = SceneCommandSupport.CreateLimitOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("game-object", "Query one indexed game object."); command.Arguments.Add(selector); command.Options.Add(children); command.Options.Add(components); command.Options.Add(refs); command.Options.Add(limit); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("game-object", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (result.GetValue(limit) <= 0) return new CommandOutput("game-object", result.GetValue(json), output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.");
            var data = service.GameObjectAsync(new GameObjectQueryRequest(null, result.GetValue(selector)!, result.GetValue(children), result.GetValue(components), result.GetValue(refs), result.GetValue(limit)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new GameObjectOutput(data.Status, data.Snapshot, data.GameObject, data.Candidates, data.Children, data.Components, data.References, data.Containers, data.Transform);
            return SceneCommandSupport.Write(new CommandOutput("game-object", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteGameObject(outputData, writer));
        })); return command;
    }
}
