using System.ComponentModel;
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

    [McpServerTool(Name = "list_scenes"), Description("List indexed Schedule I scenes and prefabs from a completed scene snapshot.")]
    public async Task<ToolEnvelope<SceneListResult>> ListScenesAsync(
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId,
        [Description("Optional document kind: Scene or Prefab.")] string? kind,
        [Description("Optional case-insensitive name fragment.")] string? query,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default)
    {
        try
        {
            var boundedLimit = BoundLimit(limit);
            var parsedKind = ParseKind(kind);
            return await WithSnapshotAsync(
                buildId,
                sceneSnapshotId,
                ct,
                async (authority, snapshot) =>
                {
                    var result = await _services.SceneQueryService.ScenesAsync(
                        new SceneListRequest(authority.ResolvedBuildId, snapshot.SceneSnapshotId, parsedKind, query, boundedLimit), ct);
                    return FromResult(authority, result.Status, result, [], "scene-list");
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<SceneListResult>("InvalidLimit", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return EnvelopeMapper.Invalid<SceneListResult>("InvalidKind", exception.Message);
        }
    }

    [McpServerTool(Name = "get_scene"), Description("Resolve one indexed Schedule I scene document.")]
    public Task<ToolEnvelope<SceneDocumentQueryResult>> GetSceneAsync(
        [Description("Exact or fuzzy scene selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId,
        [Description("Optional document kind: Scene or Prefab.")] string? kind,
        [Description("Include child game objects.")] bool includeChildren = false,
        [Description("Include components.")] bool includeComponents = false,
        [Description("Include references.")] bool includeReferences = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default) =>
        GetDocumentAsync(selector, buildId, sceneSnapshotId, kind, includeChildren, includeComponents, includeReferences, limit, ct, prefab: false);

    [McpServerTool(Name = "get_gameobject"), Description("Resolve one indexed Schedule I game object.")]
    public async Task<ToolEnvelope<GameObjectQueryResult>> GetGameObjectAsync(
        [Description("Exact or fuzzy game object selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId,
        [Description("Include child game objects.")] bool includeChildren = false,
        [Description("Include components.")] bool includeComponents = false,
        [Description("Include references.")] bool includeReferences = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default)
    {
        if (TrySelectorError(selector, out ToolEnvelope<GameObjectQueryResult> error)) return error;
        try
        {
            var boundedLimit = BoundLimit(limit);
            return await WithSnapshotAsync(buildId, sceneSnapshotId, ct, async (authority, snapshot) =>
            {
                var result = await _services.SceneQueryService.GameObjectAsync(
                    new GameObjectQueryRequest(snapshot.SceneSnapshotId, selector, includeChildren, includeComponents, includeReferences, boundedLimit), ct);
                return FromResult(authority, result.Status, result, result.Candidates.Cast<object>().ToArray(), "game-object-query");
            });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<GameObjectQueryResult>("InvalidLimit", exception.Message);
        }
    }

    [McpServerTool(Name = "get_prefab"), Description("Resolve one indexed Schedule I prefab document.")]
    public Task<ToolEnvelope<SceneDocumentQueryResult>> GetPrefabAsync(
        [Description("Exact or fuzzy prefab selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId,
        [Description("Include prefab game objects.")] bool includeObjects = false,
        [Description("Include components.")] bool includeComponents = false,
        [Description("Include references.")] bool includeReferences = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default) =>
        GetDocumentAsync(selector, buildId, sceneSnapshotId, "Prefab", includeObjects, includeComponents, includeReferences, limit, ct, prefab: true);

    [McpServerTool(Name = "get_component"), Description("Resolve one indexed Schedule I component, including its resolved code-symbol handoff when requested.")]
    public async Task<ToolEnvelope<ComponentQueryResult>> GetComponentAsync(
        [Description("Exact or fuzzy component selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional completed scene snapshot ID for the selected build.")] string? sceneSnapshotId,
        [Description("Include scene references originating at the component.")] bool includeReferences = false,
        [Description("Require the component's exact resolved code-symbol handoff.")] bool includeCode = false,
        [Description("Max results (1-500). ")] int limit = SceneQueryService.DefaultLimit,
        CancellationToken ct = default)
    {
        if (TrySelectorError(selector, out ToolEnvelope<ComponentQueryResult> error)) return error;
        try
        {
            var boundedLimit = BoundLimit(limit);
            return await WithSnapshotAsync(buildId, sceneSnapshotId, ct, async (authority, snapshot) =>
            {
                var result = await _services.SceneQueryService.ComponentAsync(
                    new ComponentQueryRequest(snapshot.SceneSnapshotId, selector, includeReferences, includeCode, boundedLimit), ct);
                return FromResult(authority, result.Status, result, result.Candidates.Cast<object>().ToArray(), "component-query");
            });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<ComponentQueryResult>("InvalidLimit", exception.Message);
        }
    }

    private async Task<ToolEnvelope<SceneDocumentQueryResult>> GetDocumentAsync(
        string selector, string? buildId, string? sceneSnapshotId, string? kind,
        bool includeChildren, bool includeComponents, bool includeReferences, int limit,
        CancellationToken ct, bool prefab)
    {
        if (TrySelectorError(selector, out ToolEnvelope<SceneDocumentQueryResult> error)) return error;
        try
        {
            var boundedLimit = BoundLimit(limit);
            var parsedKind = ParseKind(kind);
            return await WithSnapshotAsync(buildId, sceneSnapshotId, ct, async (authority, snapshot) =>
            {
                var result = prefab
                    ? await _services.SceneQueryService.PrefabAsync(new PrefabQueryRequest(snapshot.SceneSnapshotId, selector, includeChildren, includeComponents, includeReferences, boundedLimit), ct)
                    : await _services.SceneQueryService.SceneAsync(new SceneQueryRequest(snapshot.SceneSnapshotId, selector, parsedKind, includeChildren, includeComponents, includeReferences, boundedLimit), ct);
                return FromResult(authority, result.Status, result, result.Candidates.Cast<object>().ToArray(), "scene-query");
            });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<SceneDocumentQueryResult>("InvalidLimit", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return EnvelopeMapper.Invalid<SceneDocumentQueryResult>("InvalidKind", exception.Message);
        }
    }

    private async Task<ToolEnvelope<T>> WithSnapshotAsync<T>(
        string? buildId, string? sceneSnapshotId, CancellationToken ct,
        Func<InstalledBuildAuthority, SceneSnapshotRecord, Task<ToolEnvelope<T>>> onResolved) where T : class =>
        await EnvelopeMapper.WithAuthorityAsync(_services.AuthorityResolver, buildId, ct, async authority =>
        {
            var snapshot = await ResolveSnapshotAsync(authority, sceneSnapshotId, ct);
            return snapshot is null
                ? SnapshotError<T>(authority, sceneSnapshotId)
                : await onResolved(authority, snapshot);
        });

    private async Task<SceneSnapshotRecord?> ResolveSnapshotAsync(InstalledBuildAuthority authority, string? sceneSnapshotId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sceneSnapshotId))
        {
            var specified = await _services.Repository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, ct);
            return specified is not null && string.Equals(specified.BuildId, authority.ResolvedBuildId, StringComparison.Ordinal)
                ? specified
                : null;
        }

        return await _services.Repository.GetLatestCompletedSceneSnapshotAsync(authority.ResolvedBuildId!, ct);
    }

    private static ToolEnvelope<T> SnapshotError<T>(InstalledBuildAuthority authority, string? sceneSnapshotId) where T : class =>
        string.IsNullOrWhiteSpace(sceneSnapshotId)
            ? ToolEnvelope<T>.NotFound(
                EnvelopeMapper.BuildFrom(authority),
                new ToolError("NoCompletedSceneIndex", "No completed scene index exists for the requested build."),
                Derived(authority, "scene-snapshot-selection"))
            : ToolEnvelope<T>.Invalid(
                new ToolError("SceneSnapshotNotFound", "The requested scene snapshot was not found for the selected build."),
                EnvelopeMapper.BuildFrom(authority),
                Derived(authority, "scene-snapshot-selection"));

    private static ToolEnvelope<T> FromResult<T>(InstalledBuildAuthority authority, SceneQueryStatus status, T result, IReadOnlyList<object> candidates, string source) where T : class
    {
        var build = EnvelopeMapper.BuildFrom(authority);
        var provenance = Derived(authority, source);
        return status switch
        {
            SceneQueryStatus.Resolved or SceneQueryStatus.PartialRecovery or SceneQueryStatus.UnresolvedSceneReference => ToolEnvelope<T>.Resolved(build, result, provenance),
            SceneQueryStatus.NoCompletedSceneIndex => ToolEnvelope<T>.NotFound(build, new ToolError("NoCompletedSceneIndex", "No completed scene index exists for the requested build."), provenance),
            SceneQueryStatus.SceneSnapshotNotFound => ToolEnvelope<T>.Invalid(new ToolError("SceneSnapshotNotFound", "The requested scene snapshot was not found."), build, provenance),
            SceneQueryStatus.SceneNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("SceneNotFound", "No indexed scene matched the selector."), provenance),
            SceneQueryStatus.GameObjectNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("GameObjectNotFound", "No indexed game object matched the selector."), provenance),
            SceneQueryStatus.ComponentNotFound => ToolEnvelope<T>.NotFound(build, new ToolError("ComponentNotFound", "No indexed component matched the selector."), provenance),
            SceneQueryStatus.AmbiguousScene or SceneQueryStatus.AmbiguousGameObject or SceneQueryStatus.AmbiguousComponent => ToolEnvelope<T>.Ambiguous(build, candidates, provenance),
            SceneQueryStatus.UnresolvedCodeSymbol => ToolEnvelope<T>.NotFound(build, new ToolError("UnresolvedCodeSymbol", "The component has no exact resolved code symbol."), provenance),
            _ => ToolEnvelope<T>.Unavailable(new ToolError(status.ToString(), "The requested scene data is unavailable."), build, provenance)
        };
    }

    private static bool TrySelectorError<T>(string? selector, out ToolEnvelope<T> error) where T : class
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            error = null!;
            return false;
        }

        error = EnvelopeMapper.Invalid<T>("InvalidArguments", "The selector must not be blank or whitespace.");
        return true;
    }

    private static int BoundLimit(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");
        return Math.Min(limit, 500);
    }

    private static SceneDocumentKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        if (Enum.TryParse<SceneDocumentKind>(kind, ignoreCase: true, out var parsed)) return parsed;
        throw new ArgumentException("Scene kind must be Scene or Prefab.", nameof(kind));
    }

    private static ProvenanceEntry Derived(InstalledBuildAuthority authority, string source) =>
        new(ProvenanceClassification.Derived, source, authority.ResolvedBuildId, authority.ExtractionId, authority.IndexId);
}
