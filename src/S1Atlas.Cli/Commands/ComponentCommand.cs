using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class ComponentCommand
{
    public static Command Create(SceneQueryService service, IndexQueryService indexQueries, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var selector = new Argument<string>("component-id|exact-type-selector"); var refs = new Option<bool>("--refs"); var code = new Option<bool>("--code"); var limit = SceneCommandSupport.CreateLimitOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("component", "Query one indexed component."); command.Arguments.Add(selector); command.Options.Add(refs); command.Options.Add(code); command.Options.Add(limit); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("component", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (result.GetValue(limit) <= 0) return new CommandOutput("component", result.GetValue(json), output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.");
            var data = service.ComponentAsync(new ComponentQueryRequest(null, result.GetValue(selector)!, result.GetValue(refs), result.GetValue(code), result.GetValue(limit)), cancellationToken).GetAwaiter().GetResult();
            SymbolQueryResult? codeSymbol = null;
            if (result.GetValue(code) && data.Component?.ResolvedTypeSymbolId is string symbolId && data.Component.ResolvedCodeIndexId is string indexId)
            {
                codeSymbol = indexQueries.GetExactSymbolAsync(indexId, symbolId, cancellationToken).GetAwaiter().GetResult();
            }
            var outputData = new ComponentOutput(data.Status, data.Snapshot, data.Component, data.Candidates, data.References, codeSymbol, data.Containers);
            return SceneCommandSupport.Write(new CommandOutput("component", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteComponent(outputData, writer));
        })); return command;
    }
}
