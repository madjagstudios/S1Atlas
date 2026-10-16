using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Commands;

internal static class SceneCommandSupport
{
    public static Option<int> CreateLimitOption() => new("--limit") { DefaultValueFactory = _ => SceneQueryService.DefaultLimit, Description = "Maximum number of returned rows." };
    public static Option<int> CreateOffsetOption() => new("--offset") { DefaultValueFactory = _ => 0, Description = "Number of rows to skip in every returned list, for the next page." };
    public static Option<bool> CreateIncomingOption() => new("--incoming") { Description = "Include decoded script-field references elsewhere in the snapshot that point here." };
    public static Option<bool> CreateExternalOption() => new("--external") { Description = "With --incoming, keep only references from outside this object's own hierarchy." };
    public static int? InvalidPage(string name, bool json, TextWriter output, TextWriter error, int limit, int offset) =>
        limit <= 0 ? new CommandOutput(name, json, output, error).Failure(1, "InvalidLimit", "--limit must be greater than zero.") :
        offset < 0 ? new CommandOutput(name, json, output, error).Failure(1, "InvalidOffset", "--offset must not be negative.") :
        null;
    public static int? NextOffset(bool hasMore, int offset, int limit) => hasMore ? offset + limit : null;
    public static void WriteNextOffset(int? nextOffset, TextWriter writer) { if (nextOffset is not null) writer.WriteLine($"More rows: re-run with --offset {nextOffset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}."); }

    public static void WriteGameObjectList(GameObjectListOutput data, TextWriter writer)
    {
        WriteSnapshot(data.Snapshot, writer);
        if (data.Scene is not null) writer.WriteLine($"Scene filter: {data.Scene.Kind} | {data.Scene.Name} | {data.Scene.SceneId}");
        WriteCandidates(writer, data.SceneCandidates, candidate => $"{candidate.Kind} | {candidate.Name} | {candidate.SceneId} | {ContainerText(candidate.ContainerId, data.Containers)}", "scene");
        if (data.Status != SceneQueryStatus.Resolved) return;
        writer.WriteLine($"Game objects: total {data.TotalCount} | returned {data.ReturnedCount}");
        foreach (var item in data.GameObjects)
        {
            var scene = data.Scenes.SingleOrDefault(row => row.SceneId == item.SceneId);
            writer.WriteLine($"GameObject: {item.Name} | id {item.GameObjectId} | scene {scene?.Name ?? item.SceneId} ({scene?.Kind.ToString() ?? "unknown"}) | local {item.LocalFileId} | active {item.Active} | {ContainerText(item.ContainerId, data.Containers)} | recovery {item.RecoveryStatus}");
        }
        WriteNextOffset(data.NextOffset, writer);
    }

    public static void WriteComponentList(ComponentListOutput data, TextWriter writer)
    {
        WriteSnapshot(data.Snapshot, writer);
        if (data.Scene is not null) writer.WriteLine($"Scene filter: {data.Scene.Kind} | {data.Scene.Name} | {data.Scene.SceneId}");
        WriteCandidates(writer, data.SceneCandidates, candidate => $"{candidate.Kind} | {candidate.Name} | {candidate.SceneId} | {ContainerText(candidate.ContainerId, data.Containers)}", "scene");
        if (data.Status != SceneQueryStatus.Resolved) return;
        if (data.IncludedTypes.Count > 0) writer.WriteLine($"Types: {string.Join(", ", data.IncludedTypes)}");
        if (data.DerivedTypesUnavailable is not null) writer.WriteLine($"Derived types: {data.DerivedTypesUnavailable}");
        writer.WriteLine($"Components of {data.Type}: total {data.TotalCount} | returned {data.ReturnedCount} | matched by {data.MatchedBy}");
        foreach (var row in data.Components)
        {
            var component = row.Component;
            writer.WriteLine($"Component: {component.ScriptNamespace}.{component.ScriptClass} ({component.Kind}) | id {component.ComponentId} | object {row.GameObject?.Name ?? "unknown"} ({component.GameObjectId}) | {ContainerText(component.ContainerId, data.Containers)} | recovery {component.RecoveryStatus}");
            if (row.ScriptFields is { Status: SceneScriptFieldSetStatus.Unavailable } unavailable) writer.WriteLine($"  Fields: unavailable ({unavailable.UnavailableReason})");
            else if (row.ScriptFields is not null)
            {
                if (row.ScriptFields.Truncated) writer.WriteLine("  Fields: truncated (the stored field set was capped, so a requested path may be incomplete)");
                foreach (var field in row.ScriptFields.Fields) writer.WriteLine($"  Field: {field.Path} ({field.TypeName}) = {field.Value}{TargetText(field.Target)}");
            }
        }
        WriteNextOffset(data.NextOffset, writer);
    }

    public static void WriteIncoming(SceneIncomingReferencePage? incoming, IReadOnlyList<SceneContainerRecord>? containers, TextWriter writer)
    {
        if (incoming is null) return;
        writer.WriteLine($"Incoming: total {incoming.TotalCount} | returned {incoming.ReturnedCount} | from outside the target {incoming.ExternalCount} | searched {incoming.Coverage.DecodedCount} decoded field sets ({incoming.Coverage.UnavailableCount} unavailable, {incoming.Coverage.TruncatedCount} truncated, {incoming.Coverage.NotParsedCount} script owners not parsed)");
        foreach (var row in incoming.Rows)
            writer.WriteLine($"Incoming: {(row.WithinTarget ? "inside" : "outside")} | {row.SourceOwnerKind} {row.SourceType ?? "unknown"} on {row.SourceName ?? "unknown"} | id {row.SourceOwnerId} | field {row.FieldPath} ({row.FieldTypeName}) = {row.Value} | target {row.TargetKind} {row.TargetId} | {ContainerText(row.SourceContainerId, containers)} | evidence {row.Evidence}");
    }
    public static int Run(string name, bool json, TextWriter output, TextWriter error, CancellationToken cancellationToken, IAtlasRepository repository, Func<int> action) => CommandExecution.Run(() => { repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult(); return action(); }, new CommandOutput(name, json, output, error), cancellationToken);
    public static int Write<T>(CommandOutput output, T data, (string Code, string Message)? failure, Action<TextWriter> human) => failure is null ? output.Success(data, human) : output.FailureWithData(failure.Value.Code, failure.Value.Message, data, human);
    public static (string Code, string Message)? FailureFor(SceneQueryStatus status) => status switch { SceneQueryStatus.NoCompletedSceneIndex => ("NoCompletedSceneIndex", "No completed scene index exists for the requested build."), SceneQueryStatus.SceneSnapshotNotFound => ("SceneSnapshotNotFound", "The requested scene snapshot was not found."), SceneQueryStatus.SceneNotFound => ("SceneNotFound", "No indexed scene matched the selector."), SceneQueryStatus.AmbiguousScene => ("AmbiguousScene", "The scene selector matched multiple candidates. Use an exact scene ID."), SceneQueryStatus.GameObjectNotFound => ("GameObjectNotFound", "No indexed game object matched the selector."), SceneQueryStatus.AmbiguousGameObject => ("AmbiguousGameObject", "The game object selector matched multiple candidates. Use an exact game object ID."), SceneQueryStatus.ComponentNotFound => ("ComponentNotFound", "No indexed component matched the selector."), SceneQueryStatus.AmbiguousComponent => ("AmbiguousComponent", "The component selector matched multiple candidates. Use an exact component ID."), SceneQueryStatus.UnresolvedCodeSymbol => ("UnresolvedCodeSymbol", "The component has no exact resolved code symbol."), SceneQueryStatus.PartialRecovery => ("PartialRecovery", "The selected scene data is partially recovered."), SceneQueryStatus.UnresolvedSceneReference => ("UnresolvedSceneReference", "The selected scene data contains unresolved references."), SceneQueryStatus.NoRecoverableSceneObjects => ("NoRecoverableSceneObjects", "This completed scene snapshot recovered no GameObjects and cannot be queried. Rebuild with `index --scene --force`; the rerun reports why."), SceneQueryStatus.ScriptableAssetNotFound => ("ScriptableAssetNotFound", "No indexed scriptable asset matched the selector."), SceneQueryStatus.AmbiguousScriptableAsset => ("AmbiguousScriptableAsset", "The asset selector matched multiple candidates. Use an exact asset ID."), _ => null };
    public static string ContainerText(string id, IReadOnlyList<S1Atlas.Core.Scenes.SceneContainerRecord>? containers) { var container = containers?.SingleOrDefault(row => row.ContainerId == id); return container is null ? $"container metadata unavailable | id {id}" : $"{container.RelativePath} | sha256 {container.Sha256} | sidecar {container.SidecarManifest}"; }
    public static void WriteCandidates<T>(TextWriter writer, IReadOnlyList<T> candidates, Func<T, string> describe, string idNoun)
    {
        if (candidates.Count == 0) return;
        writer.WriteLine($"Found {candidates.Count} candidates.");
        for (var index = 0; index < candidates.Count; index++)
            writer.WriteLine($"{index + 1} | {describe(candidates[index])}");
        writer.WriteLine($"Hint: re-run with an exact {idNoun} ID.");
    }
    public static void WriteScene(SceneDocumentOutput data, TextWriter writer) { WriteSnapshot(data.Snapshot, writer); if (data.Scene is not null) writer.WriteLine($"Scene: {data.Scene.Kind} | {data.Scene.Name} | {data.Scene.SceneId} | local {data.Scene.SourceLocalFileId} | objects {data.Scene.ObjectCount} | roots {data.Scene.RootCount} | {ContainerText(data.Scene.ContainerId, data.Containers)} | {data.Scene.RecoveryStatus}"); WriteCandidates(writer, data.Candidates, candidate => $"{candidate.Name} | {candidate.SceneId} | {ContainerText(candidate.ContainerId, data.Containers)} | {candidate.RecoveryStatus}", "scene"); WritePages(data.Children, data.Components, data.References, data.Containers, writer); WriteNextOffset(data.NextOffset, writer); }
    public static void WriteGameObject(GameObjectOutput data, TextWriter writer) { WriteSnapshot(data.Snapshot, writer); if (data.GameObject is not null) writer.WriteLine($"GameObject: {data.GameObject.Name} | {data.GameObject.GameObjectId} | scene {data.GameObject.SceneId} | local {data.GameObject.LocalFileId} | active {data.GameObject.Active} | layer {data.GameObject.Layer} | tag {data.GameObject.Tag} | {ContainerText(data.GameObject.ContainerId, data.Containers)} | {data.GameObject.RecoveryStatus}"); if (data.Transform is not null) writer.WriteLine($"Transform: position ({Real(data.Transform.PositionX)}, {Real(data.Transform.PositionY)}, {Real(data.Transform.PositionZ)}) | rotation ({Real(data.Transform.RotationX)}, {Real(data.Transform.RotationY)}, {Real(data.Transform.RotationZ)}, {Real(data.Transform.RotationW)}) | scale ({Real(data.Transform.ScaleX)}, {Real(data.Transform.ScaleY)}, {Real(data.Transform.ScaleZ)}) | parent {data.Transform.ParentGameObjectId ?? "none"} | sibling {(data.Transform.SiblingIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")} | {data.Transform.RecoveryStatus}"); WriteCandidates(writer, data.Candidates, candidate => $"{candidate.Name} | {candidate.GameObjectId} | {ContainerText(candidate.ContainerId, data.Containers)} | {candidate.RecoveryStatus}", "game object"); WritePages(data.Children, data.Components, data.References, data.Containers, writer); WriteIncoming(data.Incoming, data.Containers, writer); WriteNextOffset(data.NextOffset, writer); }
    public static void WriteScriptableAsset(ScriptableAssetOutput data, TextWriter writer)
    {
        WriteSnapshot(data.Snapshot, writer);
        if (data.Asset is not null)
        {
            writer.WriteLine($"Asset: {data.Asset.Name} | {data.Asset.AssetId} | local {data.Asset.LocalFileId} | script {data.Asset.ScriptAssembly}/{data.Asset.ScriptNamespace}.{data.Asset.ScriptClass} | {ContainerText(data.Asset.ContainerId, data.Containers)} | symbol {data.Asset.ResolvedTypeSymbolId ?? "unresolved"} | {data.Asset.TypeResolutionStatus} | {data.Asset.RecoveryStatus}");
            WriteScriptFields(data.ScriptFields, writer);
        }

        WriteCandidates(writer, data.Candidates, candidate => $"{candidate.Name} | {candidate.AssetId} | {candidate.ScriptNamespace}.{candidate.ScriptClass} | {ContainerText(candidate.ContainerId, data.Containers)}", "asset");
    }

    public static void WriteScriptFields(SceneScriptFieldSetRecord? fields, TextWriter writer)
    {
        if (fields is null) return;
        if (fields.Status == SceneScriptFieldSetStatus.Unavailable) { writer.WriteLine($"Fields: unavailable ({fields.UnavailableReason})"); return; }
        writer.WriteLine($"Fields: {fields.Fields.Count}{(fields.Truncated ? " (truncated)" : string.Empty)}");
        foreach (var field in fields.Fields) writer.WriteLine($"Field: {field.Path} ({field.TypeName}) = {field.Value}{TargetText(field.Target)}");
    }
    private static string TargetText(S1Atlas.Core.Scenes.SceneScriptFieldTarget? target) => target?.Status switch
    {
        null => string.Empty,
        S1Atlas.Core.Scenes.SceneScriptFieldTargetStatus.Null => " -> null",
        S1Atlas.Core.Scenes.SceneScriptFieldTargetStatus.Unresolved => $" -> unresolved ({target.Reason})",
        _ => $" -> {target.Kind}{(target.Name is null ? string.Empty : $" \"{target.Name}\"")}{(target.TypeName is null ? string.Empty : $" ({target.TypeName})")} | " +
             (target.Id is null ? $"container {target.ContainerId} | local {target.LocalFileId}" : $"id {target.Id}")
    };
    public static void WriteComponent(ComponentOutput data, TextWriter writer) { WriteSnapshot(data.Snapshot, writer); if (data.Component is not null) writer.WriteLine($"Component: {data.Component.Kind} | {data.Component.ComponentId} | object {data.Component.GameObjectId} | local {data.Component.LocalFileId} | class {data.Component.UnityClassId} | script {data.Component.ScriptAssembly}/{data.Component.ScriptNamespace}.{data.Component.ScriptClass} | {ContainerText(data.Component.ContainerId, data.Containers)} | symbol {data.Component.ResolvedTypeSymbolId ?? "unresolved"} | index {data.Component.ResolvedCodeIndexId ?? "unresolved"} | {data.Component.TypeResolutionStatus} | {data.Component.RecoveryStatus}"); if (data.CodeSymbol is not null) writer.WriteLine($"Code: {data.CodeSymbol.QualifiedName} | {data.CodeSymbol.Signature} | {data.CodeSymbol.SymbolId} | index {data.CodeSymbol.IndexId}"); WriteScriptFields(data.ScriptFields, writer); WriteCandidates(writer, data.Candidates, candidate => $"{candidate.Kind} | {candidate.ComponentId} | {ContainerText(candidate.ContainerId, data.Containers)} | {candidate.RecoveryStatus}", "component"); WriteReferences(data.References, data.Containers, writer); WriteIncoming(data.Incoming, data.Containers, writer); WriteNextOffset(data.NextOffset, writer); }
    private static string Real(float? value) => value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    private static void WriteSnapshot(S1Atlas.Core.Scenes.SceneSnapshotRecord? snapshot, TextWriter writer) { if (snapshot is not null) writer.WriteLine($"Snapshot: {snapshot.SceneSnapshotId} | build {snapshot.BuildId} | extraction {snapshot.ExtractionId} | code snapshot {snapshot.CodeSnapshotId} | index {snapshot.CodeIndexId} | parser {snapshot.ParserId} {snapshot.ParserVersion} | type tree {snapshot.TypeTreeSource ?? "unknown"} | script layouts {snapshot.ScriptLayoutSource ?? "unknown"} | recovery {snapshot.RecoveryStatus}"); }
    private static void WritePages(S1Atlas.Core.Scenes.ScenePageResult<S1Atlas.Core.Scenes.SceneGameObjectRecord> children, S1Atlas.Core.Scenes.ScenePageResult<S1Atlas.Core.Scenes.SceneComponentRecord> components, S1Atlas.Core.Scenes.ScenePageResult<S1Atlas.Core.Scenes.SceneReferenceRecord> references, IReadOnlyList<S1Atlas.Core.Scenes.SceneContainerRecord>? containers, TextWriter writer) { writer.WriteLine($"Children: total {children.TotalCount} | returned {children.ReturnedCount}"); foreach (var child in children.Rows) writer.WriteLine($"Child: {child.Name} | id {child.GameObjectId} | scene {child.SceneId} | local {child.LocalFileId} | active {child.Active} | layer {child.Layer} | tag {child.Tag} | {ContainerText(child.ContainerId, containers)} | recovery {child.RecoveryStatus}"); writer.WriteLine($"Components: total {components.TotalCount} | returned {components.ReturnedCount}"); foreach (var component in components.Rows) writer.WriteLine($"Component: {component.Kind} | id {component.ComponentId} | object {component.GameObjectId} | local {component.LocalFileId} | class {component.UnityClassId} | script {component.ScriptAssembly}/{component.ScriptNamespace}.{component.ScriptClass} | {ContainerText(component.ContainerId, containers)} | symbol {component.ResolvedTypeSymbolId ?? "unresolved"} | index {component.ResolvedCodeIndexId ?? "unresolved"} | resolution {component.TypeResolutionStatus} | recovery {component.RecoveryStatus}"); WriteReferences(references, containers, writer); }
    private static void WriteReferences(S1Atlas.Core.Scenes.ScenePageResult<S1Atlas.Core.Scenes.SceneReferenceRecord> references, IReadOnlyList<S1Atlas.Core.Scenes.SceneContainerRecord>? containers, TextWriter writer) { writer.WriteLine($"References: total {references.TotalCount} | returned {references.ReturnedCount} | unresolved {references.UnresolvedCount}"); foreach (var reference in references.Rows) writer.WriteLine($"Reference: id {reference.ReferenceId} | source component {reference.SourceComponentId ?? "none"} | field {reference.FieldPath ?? "none"} | declared {reference.DeclaredType ?? "none"} | source local {reference.SourceLocalFileId} | source {ContainerText(reference.SourceContainerId, containers)} | target container {(reference.TargetContainerId is null ? "none" : ContainerText(reference.TargetContainerId, containers))} | target local {reference.TargetLocalFileId?.ToString() ?? "none"} | target object {reference.TargetGameObjectId ?? "none"} | target component {reference.TargetComponentId ?? "none"} | target symbol {reference.TargetSymbolId ?? "none"} | target text {reference.TargetText ?? "none"} | evidence {reference.Evidence} | resolution {reference.ResolutionStatus} | recovery {reference.RecoveryStatus}"); }
}
