using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class SceneCommandSupport
{
    public static Option<int> CreateLimitOption() => new("--limit") { DefaultValueFactory = _ => SceneQueryService.DefaultLimit, Description = "Maximum number of returned rows." };
    public static int Run(string name, bool json, TextWriter output, TextWriter error, CancellationToken cancellationToken, IAtlasRepository repository, Func<int> action) => CommandExecution.Run(() => { repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult(); return action(); }, new CommandOutput(name, json, output, error), cancellationToken);
    public static int Write<T>(CommandOutput output, T data, (string Code, string Message)? failure, Action<TextWriter> human) => failure is null ? output.Success(data, human) : output.Failure(1, failure.Value.Code, failure.Value.Message, data);
    public static (string Code, string Message)? FailureFor(SceneQueryStatus status) => status switch { SceneQueryStatus.NoCompletedSceneIndex => ("NoCompletedSceneIndex", "No completed scene index exists for the requested build."), SceneQueryStatus.SceneSnapshotNotFound => ("SceneSnapshotNotFound", "The requested scene snapshot was not found."), SceneQueryStatus.SceneNotFound => ("SceneNotFound", "No indexed scene matched the selector."), SceneQueryStatus.AmbiguousScene => ("AmbiguousScene", "The scene selector matched multiple candidates. Use an exact scene ID."), SceneQueryStatus.GameObjectNotFound => ("GameObjectNotFound", "No indexed game object matched the selector."), SceneQueryStatus.AmbiguousGameObject => ("AmbiguousGameObject", "The game object selector matched multiple candidates. Use an exact game object ID."), SceneQueryStatus.ComponentNotFound => ("ComponentNotFound", "No indexed component matched the selector."), SceneQueryStatus.AmbiguousComponent => ("AmbiguousComponent", "The component selector matched multiple candidates. Use an exact component ID."), SceneQueryStatus.UnresolvedCodeSymbol => ("UnresolvedCodeSymbol", "The component has no exact resolved code symbol."), _ => null };
    public static void WriteScene(SceneDocumentOutput data, TextWriter writer) { if (data.Scene is not null) writer.WriteLine($"{data.Scene.Kind} | {data.Scene.Name} | {data.Scene.SceneId} | local {data.Scene.SourceLocalFileId} | {data.Scene.RecoveryStatus}"); }
    public static void WriteGameObject(GameObjectOutput data, TextWriter writer) { if (data.GameObject is not null) writer.WriteLine($"{data.GameObject.Name} | {data.GameObject.GameObjectId} | local {data.GameObject.LocalFileId} | {data.GameObject.RecoveryStatus}"); }
    public static void WriteComponent(ComponentOutput data, TextWriter writer) { if (data.Component is not null) writer.WriteLine($"{data.Component.Kind} | {data.Component.ComponentId} | local {data.Component.LocalFileId} | symbol {data.Component.ResolvedTypeSymbolId ?? "unresolved"} | {data.Component.RecoveryStatus}"); }
}
