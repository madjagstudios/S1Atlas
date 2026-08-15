using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class SceneCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("scene-id|exact-name"); var children = new Option<bool>("--children"); var components = new Option<bool>("--components"); var refs = new Option<bool>("--refs"); var limit = SceneCommandSupport.CreateLimitOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("scene", "Query one indexed scene document."); command.Arguments.Add(selector); command.Options.Add(children); command.Options.Add(components); command.Options.Add(refs); command.Options.Add(limit); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("scene", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (result.GetValue(limit) <= 0) return new CommandOutput("scene", result.GetValue(json), output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.");
            var data = service.SceneAsync(new SceneQueryRequest(null, result.GetValue(selector)!, null, result.GetValue(children), result.GetValue(components), result.GetValue(refs), result.GetValue(limit)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new SceneDocumentOutput(data.Status, data.Snapshot, data.Scene, data.Candidates, data.Children, data.Components, data.References, data.Containers);
            return SceneCommandSupport.Write(new CommandOutput("scene", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteScene(outputData, writer));
        })); return command;
    }
}
