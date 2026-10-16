using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class ComponentsCommand
{
    public static Command Create(SceneQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var type = new Argument<string>("exact-type") { Description = "Exact Namespace.Class (for example ScheduleOne.Vehicles.LandVehicle) or Unity kind (for example Transform)." }; var derived = new Option<bool>("--derived") { Description = "Also include components whose type derives from or implements this type in the code index." }; var fields = new Option<string?>("--fields") { Description = "Comma-separated decoded script-field paths to show per component; a path also selects the leaves nested under it." }; var scene = new Option<string?>("--scene") { Description = "Optional scene or prefab document: scene ID or exact name." }; var build = new Option<string?>("--build") { Description = "Build ID; omit for current." }; var snapshot = new Option<string?>("--snapshot") { Description = "Optional completed scene snapshot ID for the selected build." };
        var limit = SceneCommandSupport.CreateLimitOption(); var offset = SceneCommandSupport.CreateOffsetOption(); var json = CommandOutput.CreateJsonOption();
        var command = new Command("components", CliExamples.With("List indexed components of one type with selected decoded field values.", "s1atlas components ScheduleOne.Vehicles.LandVehicle --fields vehicleName,vehicleCode,vehiclePrice", "s1atlas components ScheduleOne.Vehicles.LandVehicle --derived --scene Main"));
        command.Arguments.Add(type); command.Options.Add(derived); command.Options.Add(fields); command.Options.Add(scene); command.Options.Add(build); command.Options.Add(snapshot); command.Options.Add(limit); command.Options.Add(offset); command.Options.Add(json);
        command.SetAction(result => SceneCommandSupport.Run("components", result.GetValue(json), output, error, cancellationToken, repository, () =>
        {
            if (SceneCommandSupport.InvalidPage("components", result.GetValue(json), output, error, result.GetValue(limit), result.GetValue(offset)) is int invalid) return invalid;
            var paths = result.GetValue(fields)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            var data = service.ComponentsAsync(new ComponentListRequest(result.GetValue(type)!, result.GetValue(build), result.GetValue(snapshot), result.GetValue(derived), paths, result.GetValue(scene), result.GetValue(limit), result.GetValue(offset)), cancellationToken).GetAwaiter().GetResult();
            var outputData = new ComponentListOutput(data.Status, data.Snapshot, data.Scene, data.SceneCandidates, data.Type, data.MatchedBy, data.IncludedTypes, data.DerivedTypesUnavailable, data.Page.TotalCount, data.Page.ReturnedCount, data.Page.Rows, data.Containers, SceneCommandSupport.NextOffset(data.HasMore, result.GetValue(offset), result.GetValue(limit)));
            return SceneCommandSupport.Write(new CommandOutput("components", result.GetValue(json), output, error), outputData, SceneCommandSupport.FailureFor(data.Status, result.GetValue(build), repository, cancellationToken), writer => SceneCommandSupport.WriteComponentList(outputData, writer));
        }));
        return command;
    }
}
