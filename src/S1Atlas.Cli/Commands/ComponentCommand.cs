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
        var selector = new Argument<string>("component-id|exact-type-selector") { Description = "Component ID or exact component type." }; var refs = new Option<bool>("--refs") { Description = "Include references." }; var code = new Option<bool>("--code") { Description = "Include the exact resolved code symbol for the component type." }; var incoming = SceneCommandSupport.CreateIncomingOption(); var external = SceneCommandSupport.CreateExternalOption(); var limit = SceneCommandSupport.CreateLimitOption(); var offset = SceneCommandSupport.CreateOffsetOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("component", CliExamples.With("Query one indexed component.", "s1atlas component <id> --code")); command.Arguments.Add(selector); command.Options.Add(refs); command.Options.Add(code); command.Options.Add(incoming); command.Options.Add(external); command.Options.Add(limit); command.Options.Add(offset); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("component", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (SceneCommandSupport.InvalidPage("component", result.GetValue(json), output, error, result.GetValue(limit), result.GetValue(offset)) is int invalid) return invalid;
            var data = service.ComponentAsync(new ComponentQueryRequest(null, result.GetValue(selector)!, result.GetValue(refs), result.GetValue(code), result.GetValue(limit), result.GetValue(incoming), result.GetValue(external), result.GetValue(offset)), cancellationToken).GetAwaiter().GetResult();
            SymbolQueryResult? codeSymbol = null;
            if (result.GetValue(code) && data.Component?.ResolvedTypeSymbolId is string symbolId && data.Component.ResolvedCodeIndexId is string indexId)
            {
                codeSymbol = indexQueries.GetExactSymbolAsync(indexId, symbolId, cancellationToken).GetAwaiter().GetResult();
            }
            var outputData = new ComponentOutput(data.Status, data.Snapshot, data.Component, data.Candidates, data.References, codeSymbol, data.Containers, data.ScriptFields, data.Incoming, SceneCommandSupport.NextOffset(data.HasMore, result.GetValue(offset), result.GetValue(limit)));
            return SceneCommandSupport.Write(new CommandOutput("component", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer => SceneCommandSupport.WriteComponent(outputData, writer));
        })); return command;
    }
}
