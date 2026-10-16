using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Scenes;
using S1Atlas.Indexing.Scene;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class SceneTools
{
    private readonly McpReadOnlyServices _services;

    public SceneTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "list_scenes", Title = "List scenes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List indexed scenes and prefabs from a completed scene snapshot.")]
    public async Task<ToolEnvelope<SceneListResult>> ListScenesAsync(
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Optional document kind filter.")] SceneDocumentKind? kind = null,
        [Description("Optional case-insensitive name fragment.")] string? query = null,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            try
            {
                var boundedLimit = BoundLimit(limit);
                if (!CodeSymbolTools.ToolArguments.TryDecodeCursor<SceneListResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                var parsedKind = kind;
                return await WithSnapshotForAuthorityAsync(
                authority,
                sceneSnapshotId,
                ct,
                async (authority, snapshot) =>
                {
                    var result = await _services.SceneQueryService.ScenesAsync(
                        new SceneListRequest(authority.ResolvedBuildId, snapshot.SceneSnapshotId, parsedKind, query, boundedLimit, offset), ct);
                    var expectedHash = CodeSymbolTools.ToolArguments.CursorHashFor(
                        "list_scenes", authority.ResolvedBuildId, snapshot.SceneSnapshotId,
                        buildId, sceneSnapshotId, parsedKind?.ToString(), query, boundedLimit.ToString());
                    if (!CodeSymbolTools.ToolArguments.VerifyCursorHash<SceneListResult>(cursorHash, expectedHash, authority, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return FromResult(authority, result.Status, nextCursor is null ? result : result with { NextCursor = nextCursor }, [], "scene-list");
                });
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<SceneListResult>(authority, "InvalidLimit", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return Invalid<SceneListResult>(authority, "InvalidKind", exception.Message);
            }
        });
    }

    [McpServerTool(Name = "get_scene", Title = "Get scene", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one indexed Schedule I scene document.")]
    public Task<ToolEnvelope<SceneDocumentQueryResult>> GetSceneAsync(
        [Description("Exact or fuzzy scene selector.")] string selector,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Document kind: Scene (default) or Prefab.")] SceneDocumentKind? kind = null,
        [Description("Include child game objects.")] bool includeChildren = false,
        [Description("Include components.")] bool includeComponents = false,
        [Description("Include references.")] bool includeReferences = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Cursor for the next page of every included list; reuse arguments verbatim.")] string? cursor = null) =>
        GetDocumentAsync(selector, buildId, sceneSnapshotId, kind, includeChildren, includeComponents, includeReferences, limit, cursor, ct);

    [McpServerTool(Name = "get_gameobject", Title = "Get game object", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one indexed Schedule I game object by ID or scene-id/exact-name. Use list_gameobjects to find objects by a name fragment.")]
    public async Task<ToolEnvelope<GameObjectQueryResult>> GetGameObjectAsync(
        [Description("Exact or fuzzy game object selector.")] string selector,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Include child game objects.")] bool includeChildren = false,
        [Description("Include components.")] bool includeComponents = false,
        [Description("Include references.")] bool includeReferences = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Include decoded script-field references in the snapshot that point at this object or one of its components.")] bool includeIncoming = false,
        [Description("With includeIncoming, keep only references from outside this object's own hierarchy.")] bool externalOnly = false,
        [Description("Cursor for the next page of every included list; reuse arguments verbatim.")] string? cursor = null)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            if (TrySelectorError(selector, authority, out ToolEnvelope<GameObjectQueryResult> error)) return error;
            try
            {
                var boundedLimit = BoundLimit(limit);
                return await PagedAsync(authority, sceneSnapshotId, cursor, "get_gameobject", boundedLimit,
                    [buildId, sceneSnapshotId, selector, Flag(includeChildren), Flag(includeComponents), Flag(includeReferences), Flag(includeIncoming), Flag(externalOnly)],
                    (snapshot, offset) => _services.SceneQueryService.GameObjectAsync(
                        new GameObjectQueryRequest(snapshot.SceneSnapshotId, selector, includeChildren, includeComponents, includeReferences, boundedLimit, includeIncoming, externalOnly, offset), ct),
                    result => result.HasMore, (result, next) => result with { NextCursor = next },
                    (resolved, result) => FromResult(resolved, result.Status, result, result.Candidates.Cast<object>().ToArray(), "game-object-query"), ct);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<GameObjectQueryResult>(authority, "InvalidLimit", exception.Message);
            }
        });
    }

    [McpServerTool(Name = "list_gameobjects", Title = "List game objects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find indexed game objects whose name contains a case-insensitive fragment, across the scene snapshot or within one scene or prefab document. Paged.")]
    public async Task<ToolEnvelope<GameObjectListResult>> ListGameObjectsAsync(
        [Description("Case-insensitive name fragment; omit to list every object in scope.")] string? query = null,
        [Description("Optional scene or prefab document: scene ID or exact name.")] string? scene = null,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            try
            {
                var boundedLimit = BoundLimit(limit);
                return await PagedAsync(authority, sceneSnapshotId, cursor, "list_gameobjects", boundedLimit,
                    [buildId, sceneSnapshotId, query, scene],
                    (snapshot, offset) => _services.SceneQueryService.GameObjectsAsync(
                        new GameObjectListRequest(authority.ResolvedBuildId, snapshot.SceneSnapshotId, query, scene, boundedLimit, offset), ct),
                    result => result.HasMore, (result, next) => result with { NextCursor = next },
                    (resolved, result) => FromResult(resolved, result.Status, result, result.SceneCandidates.Cast<object>().ToArray(), "game-object-list"), ct);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<GameObjectListResult>(authority, "InvalidLimit", exception.Message);
            }
        });
    }

    [McpServerTool(Name = "get_scriptable_object", Title = "Get ScriptableObject", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one asset-level Schedule I ScriptableObject (no GameObject) by asset ID, exact asset name, or exact Namespace.Class, with decoded script field values when restored.")]
    public async Task<ToolEnvelope<ScriptableAssetQueryResult>> GetScriptableObjectAsync(
        [Description("Asset ID, exact asset name (m_Name), or exact Namespace.Class.")] string selector,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        CancellationToken ct = default)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            if (TrySelectorError(selector, authority, out ToolEnvelope<ScriptableAssetQueryResult> error)) return error;
            return await WithSnapshotForAuthorityAsync(authority, sceneSnapshotId, ct, async (resolvedAuthority, snapshot) =>
            {
                var result = await _services.SceneQueryService.ScriptableAssetAsync(
                    new ScriptableAssetQueryRequest(snapshot.SceneSnapshotId, selector), ct);
                return FromResult(resolvedAuthority, result.Status, result, result.Candidates.Cast<object>().ToArray(), "scriptable-asset-query", DecodedFieldsFact(resolvedAuthority, result.ScriptFields));
            });
        });
    }

    [McpServerTool(Name = "get_component", Title = "Get component", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one indexed Schedule I component with decoded script field values when restored, plus its resolved code-symbol handoff when requested. Use list_components to list every component of a type.")]
    public async Task<ToolEnvelope<ComponentQueryResult>> GetComponentAsync(
        [Description("Exact or fuzzy component selector.")] string selector,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Include scene references originating at the component.")] bool includeReferences = false,
        [Description("Require the component's exact resolved code-symbol handoff.")] bool includeCode = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Include decoded script-field references in the snapshot that point at this component.")] bool includeIncoming = false,
        [Description("With includeIncoming, keep only references from outside the component's own game object hierarchy.")] bool externalOnly = false,
        [Description("Cursor for the next page of every included list; reuse arguments verbatim.")] string? cursor = null)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            if (TrySelectorError(selector, authority, out ToolEnvelope<ComponentQueryResult> error)) return error;
            try
            {
                var boundedLimit = BoundLimit(limit);
                return await PagedAsync(authority, sceneSnapshotId, cursor, "get_component", boundedLimit,
                    [buildId, sceneSnapshotId, selector, Flag(includeReferences), Flag(includeCode), Flag(includeIncoming), Flag(externalOnly)],
                    (snapshot, offset) => _services.SceneQueryService.ComponentAsync(
                        new ComponentQueryRequest(snapshot.SceneSnapshotId, selector, includeReferences, includeCode, boundedLimit, includeIncoming, externalOnly, offset), ct),
                    result => result.HasMore, (result, next) => result with { NextCursor = next },
                    (resolved, result) => FromResult(resolved, result.Status, result, result.Candidates.Cast<object>().ToArray(), "component-query", DecodedFieldsFact(resolved, result.ScriptFields)), ct);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<ComponentQueryResult>(authority, "InvalidLimit", exception.Message);
            }
        });
    }

    [McpServerTool(Name = "list_components", Title = "List components", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List indexed components of one type (exact Namespace.Class or Unity kind), optionally including types derived from it, each with its game object and only the requested decoded script-field paths. Paged.")]
    public async Task<ToolEnvelope<ComponentListResult>> ListComponentsAsync(
        [Description("Exact Namespace.Class (for example ScheduleOne.Vehicles.LandVehicle) or Unity kind (for example Transform).")] string type,
        [Description("Also include components whose type derives from or implements this type in the code index.")] bool includeDerived = false,
        [Description("Decoded script-field paths to return per component; a path also selects the leaves nested under it. Omit for none.")] string[]? fields = null,
        [Description("Optional scene or prefab document: scene ID or exact name.")] string? scene = null,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId = null,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            if (TrySelectorError(type, authority, out ToolEnvelope<ComponentListResult> error)) return error;
            try
            {
                var boundedLimit = BoundLimit(limit);
                var paths = fields?.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                return await PagedAsync(authority, sceneSnapshotId, cursor, "list_components", boundedLimit,
                    [buildId, sceneSnapshotId, type, Flag(includeDerived), paths is null ? null : string.Join('\n', paths), scene],
                    (snapshot, offset) => _services.SceneQueryService.ComponentsAsync(
                        new ComponentListRequest(type, authority.ResolvedBuildId, snapshot.SceneSnapshotId, includeDerived, paths, scene, boundedLimit, offset), ct),
                    result => result.HasMore, (result, next) => result with { NextCursor = next },
                    (resolved, result) => FromResult(resolved, result.Status, result, result.SceneCandidates.Cast<object>().ToArray(), "component-list",
                        result.Page.Rows.Any(row => row.ScriptFields?.Status == SceneScriptFieldSetStatus.Decoded) ? Fact(resolved, "serialized-script-fields") : null), ct);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<ComponentListResult>(authority, "InvalidLimit", exception.Message);
            }
        });
    }

    private async Task<ToolEnvelope<SceneDocumentQueryResult>> GetDocumentAsync(
        string selector, string? buildId, string? sceneSnapshotId, SceneDocumentKind? kind,
        bool includeChildren, bool includeComponents, bool includeReferences, int limit, string? cursor,
        CancellationToken ct)
    {
        return await WithAuthorityAsync(buildId, ct, async authority =>
        {
            if (TrySelectorError(selector, authority, out ToolEnvelope<SceneDocumentQueryResult> error)) return error;
            try
            {
                var boundedLimit = BoundLimit(limit);
                var parsedKind = kind ?? SceneDocumentKind.Scene;
                return await PagedAsync(authority, sceneSnapshotId, cursor, "get_scene", boundedLimit,
                    [buildId, sceneSnapshotId, selector, parsedKind.ToString(), Flag(includeChildren), Flag(includeComponents), Flag(includeReferences)],
                    (snapshot, offset) => _services.SceneQueryService.SceneAsync(
                        new SceneQueryRequest(snapshot.SceneSnapshotId, selector, parsedKind, includeChildren, includeComponents, includeReferences, boundedLimit, offset), ct),
                    result => result.HasMore, (result, next) => result with { NextCursor = next },
                    (resolved, result) => FromResult(resolved, result.Status, result, result.Candidates.Cast<object>().ToArray(), "scene-query"), ct);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Invalid<SceneDocumentQueryResult>(authority, "InvalidLimit", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return Invalid<SceneDocumentQueryResult>(authority, "InvalidKind", exception.Message);
            }
        });
    }

    // Decodes the page cursor, checks it was minted for these arguments on this build and
    // snapshot, runs the query at its offset, and mints the next cursor when more rows remain.
    private async Task<ToolEnvelope<T>> PagedAsync<T>(
        InstalledBuildAuthority authority,
        string? sceneSnapshotId,
        string? cursor,
        string tool,
        int boundedLimit,
        string?[] arguments,
        Func<SceneSnapshotRecord, int, Task<T>> query,
        Func<T, bool> hasMore,
        Func<T, string, T> withCursor,
        Func<InstalledBuildAuthority, T, ToolEnvelope<T>> envelope,
        CancellationToken ct) where T : class
    {
        if (!CodeSymbolTools.ToolArguments.TryDecodeCursor<T>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
            return cursorError;
        return await WithSnapshotForAuthorityAsync(authority, sceneSnapshotId, ct, async (resolved, snapshot) =>
        {
            var expectedHash = CodeSymbolTools.ToolArguments.CursorHashFor(
                tool, resolved.ResolvedBuildId, snapshot.SceneSnapshotId, [.. arguments, boundedLimit.ToString(CultureInfo.InvariantCulture)]);
            if (!CodeSymbolTools.ToolArguments.VerifyCursorHash<T>(cursorHash, expectedHash, resolved, out var hashError))
                return hashError;
            var result = await query(snapshot, offset);
            var next = McpPageCursor.MintNextCursor(hasMore(result), expectedHash, offset, boundedLimit);
            return envelope(resolved, next is null ? result : withCursor(result, next));
        });
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private Task<ToolEnvelope<T>> WithAuthorityAsync<T>(
        string? buildId,
        CancellationToken ct,
        Func<InstalledBuildAuthority, Task<ToolEnvelope<T>>> onResolved) where T : class =>
        EnvelopeMapper.WithAuthorityAsync(_services.AuthorityResolver, buildId, ct, onResolved);

    private async Task<ToolEnvelope<T>> WithSnapshotForAuthorityAsync<T>(
        InstalledBuildAuthority authority,
        string? sceneSnapshotId,
        CancellationToken ct,
        Func<InstalledBuildAuthority, SceneSnapshotRecord, Task<ToolEnvelope<T>>> onResolved) where T : class
    {
        var resolution = await ResolveSnapshotAsync(authority, sceneSnapshotId, ct);
        return resolution.Snapshot is null
            ? SnapshotError<T>(authority, sceneSnapshotId, resolution.AuthorityMismatch)
            : await onResolved(authority, resolution.Snapshot);
    }

    private async Task<SceneSnapshotResolution> ResolveSnapshotAsync(InstalledBuildAuthority authority, string? sceneSnapshotId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sceneSnapshotId))
        {
            var specified = await _services.Repository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, ct);
            return specified is null
                ? new SceneSnapshotResolution(null, false)
                : SnapshotMatchesAuthority(specified, authority)
                    ? new SceneSnapshotResolution(specified, false)
                    : new SceneSnapshotResolution(null, true);
        }

        var latest = await _services.Repository.GetLatestCompletedSceneSnapshotAsync(authority.ResolvedBuildId!, ct);
        return latest is null
            ? new SceneSnapshotResolution(null, false)
            : SnapshotMatchesAuthority(latest, authority)
                ? new SceneSnapshotResolution(latest, false)
                : new SceneSnapshotResolution(null, true);
    }

    private static ToolEnvelope<T> SnapshotError<T>(InstalledBuildAuthority authority, string? sceneSnapshotId, bool authorityMismatch) where T : class =>
        string.IsNullOrWhiteSpace(sceneSnapshotId)
            ? authorityMismatch
                ? ToolEnvelope<T>.Unavailable(
                    new ToolError("SceneSnapshotNotFound", "The completed scene snapshot does not match the preferred verified extraction and index."),
                    EnvelopeMapper.BuildFrom(authority),
                    Derived(authority, "scene-snapshot-selection"))
                : ToolEnvelope<T>.NotFound(
                EnvelopeMapper.BuildFrom(authority),
                new ToolError("NoCompletedSceneIndex", "No completed scene index exists for the requested build."),
                Derived(authority, "scene-snapshot-selection"))
            : ToolEnvelope<T>.Invalid(
                new ToolError("SceneSnapshotNotFound", "The requested scene snapshot was not found for the selected build."),
                EnvelopeMapper.BuildFrom(authority),
                Derived(authority, "scene-snapshot-selection"));

    private static bool SnapshotMatchesAuthority(SceneSnapshotRecord snapshot, InstalledBuildAuthority authority) =>
        string.Equals(snapshot.BuildId, authority.ResolvedBuildId, StringComparison.Ordinal) &&
        string.Equals(snapshot.ExtractionId, authority.ExtractionId, StringComparison.Ordinal) &&
        string.Equals(snapshot.CodeIndexId, authority.IndexId, StringComparison.Ordinal);

    private sealed record SceneSnapshotResolution(SceneSnapshotRecord? Snapshot, bool AuthorityMismatch);

    private static ToolEnvelope<T> FromResult<T>(InstalledBuildAuthority authority, SceneQueryStatus status, T result, IReadOnlyList<object> candidates, string source, ProvenanceEntry? fact = null) where T : class
    {
        var build = EnvelopeMapper.BuildFrom(authority);
        var provenance = Derived(authority, source);
        return status switch
        {
            SceneQueryStatus.Resolved or SceneQueryStatus.PartialRecovery or SceneQueryStatus.UnresolvedSceneReference =>
                fact is null ? ToolEnvelope<T>.Resolved(build, result, provenance) : ToolEnvelope<T>.Resolved(build, result, fact, provenance),
            SceneQueryStatus.NoCompletedSceneIndex => ToolEnvelope<T>.NotFound(build, new ToolError("NoCompletedSceneIndex", "No completed scene index exists for the requested build."), provenance),
            SceneQueryStatus.SceneSnapshotNotFound => ToolEnvelope<T>.Invalid(new ToolError("SceneSnapshotNotFound", "The requested scene snapshot was not found."), build, provenance),
            SceneQueryStatus.SceneNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("SceneNotFound", "No indexed scene matched the selector."), provenance),
            SceneQueryStatus.GameObjectNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("GameObjectNotFound", "No indexed game object matched the selector."), provenance),
            SceneQueryStatus.ComponentNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("ComponentNotFound", "No indexed component matched the selector."), provenance),
            SceneQueryStatus.AmbiguousScene or SceneQueryStatus.AmbiguousGameObject or SceneQueryStatus.AmbiguousComponent => ToolEnvelope<T>.Ambiguous(build, candidates, provenance),
            SceneQueryStatus.UnresolvedCodeSymbol => ToolEnvelope<T>.NotFound(build, new ToolError("UnresolvedCodeSymbol", "The component has no exact resolved code symbol."), provenance),
            SceneQueryStatus.NoRecoverableSceneObjects => ToolEnvelope<T>.Unavailable(new ToolError("NoRecoverableSceneObjects", "This completed scene snapshot recovered no GameObjects and cannot be queried. Rebuild it with the CLI (index --scene --force); the rerun reports why."), build, provenance),
            SceneQueryStatus.ScriptableAssetNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("ScriptableAssetNotFound", "No indexed scriptable asset matched the selector."), provenance),
            SceneQueryStatus.AmbiguousScriptableAsset => ToolEnvelope<T>.Ambiguous(build, candidates, provenance),
            _ => ToolEnvelope<T>.Unavailable(new ToolError(status.ToString(), "The requested scene data is unavailable."), build, provenance)
        };
    }

    private static bool TrySelectorError<T>(string? selector, InstalledBuildAuthority authority, out ToolEnvelope<T> error) where T : class
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            error = null!;
            return false;
        }

        error = Invalid<T>(authority, "InvalidArguments", "The selector must not be blank or whitespace.");
        return true;
    }

    private static int BoundLimit(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");
        return Math.Min(limit, 500);
    }

    private static ToolEnvelope<T> Invalid<T>(InstalledBuildAuthority authority, string code, string message) where T : class =>
        ToolEnvelope<T>.Invalid(
            new ToolError(code, message),
            EnvelopeMapper.BuildFrom(authority),
            Fact(authority, "installed-build-authority"),
            Derived(authority, "tool-argument-validation"));

    // Decoded values are read from the file, not inferred, so they are FACT.
    private static ProvenanceEntry? DecodedFieldsFact(InstalledBuildAuthority authority, SceneScriptFieldSetRecord? fields) =>
        fields?.Status == SceneScriptFieldSetStatus.Decoded ? Fact(authority, "serialized-script-fields") : null;

    private static ProvenanceEntry Fact(InstalledBuildAuthority authority, string source) =>
        new(ProvenanceClassification.Fact, source, authority.ResolvedBuildId, authority.ExtractionId, authority.IndexId);

    private static ProvenanceEntry Derived(InstalledBuildAuthority authority, string source) =>
        new(ProvenanceClassification.Derived, source, authority.ResolvedBuildId, authority.ExtractionId, authority.IndexId);
}
