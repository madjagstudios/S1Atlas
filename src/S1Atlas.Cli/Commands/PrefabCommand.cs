using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class PrefabCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("prefab-id|exact-name"); var objects = new Option<bool>("--objects"); var components = new Option<bool>("--components"); var refs = new Option<bool>("--refs"); var limit = SceneCommandSupport.CreateLimitOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("prefab", "Query one proven prefab document."); command.Arguments.Add(selector); command.Options.Add(objects); command.Options.Add(components); command.Options.Add(refs); command.Options.Add(limit); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("prefab", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (result.GetValue(limit) <= 0) return new CommandOutput("prefab", result.GetValue(json), output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.");
            var data = service.PrefabAsync(new PrefabQueryRequest(null, result.GetValue(selector)!, result.GetValue(objects), result.GetValue(components), result.GetValue(refs), result.GetValue(limit)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new SceneDocumentOutput(data.Status, data.Snapshot, data.Scene, data.Candidates, data.Children, data.Components, data.References);
            return SceneCommandSupport.Write(new CommandOutput("prefab", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteScene(outputData, writer));
        })); return command;
    }
}
