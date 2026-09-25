using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class ScriptableObjectCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("asset-id|exact-name|namespace.class"); var json = CommandOutput.CreateJsonOption();
        var command = new Command("scriptable-object", "Query one indexed scriptable asset (ScriptableObject) and its decoded fields."); command.Arguments.Add(selector); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("scriptable-object", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            var data = service.ScriptableAssetAsync(new ScriptableAssetQueryRequest(null, result.GetValue(selector)!), cancellationToken).GetAwaiter().GetResult();
            var outputData = new ScriptableAssetOutput(data.Status, data.Snapshot, data.Asset, data.Candidates, data.ScriptFields, data.Containers);
            return SceneCommandSupport.Write(new CommandOutput("scriptable-object", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteScriptableAsset(outputData, writer));
        })); return command;
    }
}
