using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class GameObjectCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("game-object-id|scene-id/name") { Description = "Game object ID or scene-id/name." }; var children = new Option<bool>("--children") { Description = "Include child game objects." }; var components = new Option<bool>("--components") { Description = "Include components." }; var refs = new Option<bool>("--refs") { Description = "Include references." }; var incoming = SceneCommandSupport.CreateIncomingOption(); var external = SceneCommandSupport.CreateExternalOption(); var limit = SceneCommandSupport.CreateLimitOption(); var offset = SceneCommandSupport.CreateOffsetOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("game-object", CliExamples.With("Query one indexed game object.", "s1atlas game-object <id> --components", "s1atlas game-object <id> --incoming --external")); command.Arguments.Add(selector); command.Options.Add(children); command.Options.Add(components); command.Options.Add(refs); command.Options.Add(incoming); command.Options.Add(external); command.Options.Add(limit); command.Options.Add(offset); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("game-object", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (SceneCommandSupport.InvalidPage("game-object", result.GetValue(json), output, error, result.GetValue(limit), result.GetValue(offset)) is int invalid) return invalid;
            var data = service.GameObjectAsync(new GameObjectQueryRequest(null, result.GetValue(selector)!, result.GetValue(children), result.GetValue(components), result.GetValue(refs), result.GetValue(limit), result.GetValue(incoming), result.GetValue(external), result.GetValue(offset)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new GameObjectOutput(data.Status, data.Snapshot, data.GameObject, data.Candidates, data.Children, data.Components, data.References, data.Containers, data.Transform, data.Incoming, SceneCommandSupport.NextOffset(data.HasMore, result.GetValue(offset), result.GetValue(limit)));
            return SceneCommandSupport.Write(new CommandOutput("game-object", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteGameObject(outputData, writer));
        })); return command;
    }
}
