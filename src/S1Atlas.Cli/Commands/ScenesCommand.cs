using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class ScenesCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var build = new Option<string?>("--build"); var snapshot = new Option<string?>("--snapshot"); var kind = new Option<string?>("--kind"); var query = new Option<string?>("--query");
        var limit = SceneCommandSupport.CreateLimitOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("scenes", "List indexed scene and proven prefab documents.");
        command.Options.Add(build); command.Options.Add(snapshot); command.Options.Add(kind); command.Options.Add(query); command.Options.Add(limit); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("scenes", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (result.GetValue(limit) <= 0) return new CommandOutput("scenes", result.GetValue(json), output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.");
            if (!TryKind(result.GetValue(kind), out var parsedKind)) return new CommandOutput("scenes", result.GetValue(json), output, error).Failure(1, "InvalidKind", "--kind must be scene or prefab.");
            var data = service.ScenesAsync(new SceneListRequest(result.GetValue(build), result.GetValue(snapshot), parsedKind, result.GetValue(query), result.GetValue(limit)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new SceneListOutput(data.Status, data.Snapshot, data.Page.TotalCount, data.Page.ReturnedCount, data.Page.Rows);
            return SceneCommandSupport.Write(new CommandOutput("scenes", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status), writer =>
            {
                writer.WriteLine($"Found {outputData.TotalCount} scenes. Showing {outputData.ReturnedCount}.");
                foreach (var scene in outputData.Scenes) writer.WriteLine($"{scene.Kind} | {scene.Name} | {scene.SceneId} | local {scene.SourceLocalFileId} | {scene.RecoveryStatus}");
            });
        }));
        return command;
    }
    private static bool TryKind(string? value, out SceneDocumentKind? kind) { kind = value?.ToLowerInvariant() switch { null => null, "scene" => SceneDocumentKind.Scene, "prefab" => SceneDocumentKind.Prefab, _ => null }; return value is null || kind is not null; }
}
