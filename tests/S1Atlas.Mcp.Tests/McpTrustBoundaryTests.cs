using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using S1Atlas.Mcp;
using S1Atlas.Mcp.Tools;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class McpTrustBoundaryTests
{
    [Fact]
    public async Task StdioHost_UsesProtocolOnlyStdoutAndRegistersEveryV1Tool()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildWithScenesAsync();

        var tools = await McpTestHost.ListToolsThroughStdioAsync(atlas.DataRoot);

        Assert.Equal(
            [
                "compare_symbol",
                "find_callers",
                "find_references",
                "find_related_types",
                "get_component",
                "get_environment",
                "get_gameobject",
                "get_method",
                "get_prefab",
                "get_scene",
                "get_source",
                "get_type",
                "list_builds",
                "list_scenes",
                "search_symbols"
            ],
            tools.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void McpHost_WiresOnlyStdioAndReadOnlyServices()
    {
        var sources = McpTestHost.ReadHostWiringSources();

        Assert.Contains("WithStdioServerTransport", sources, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyAtlasComposition", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("S1Atlas.Cli", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Diagnostics.Process", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsScheduleOneLocator", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLocator", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("Installer", sources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioHost_CallTool_ReturnsSerializedAuthorityEnvelope()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildWithScenesAsync();

        var serialized = await McpTestHost.CallSearchSymbolsThroughStdioAsync(atlas.DataRoot);

        using var result = JsonDocument.Parse(serialized);
        var root = result.RootElement;
        Assert.Equal("Resolved", root.GetProperty("status").GetString());
        Assert.Equal(atlas.BuildIdB, root.GetProperty("build").GetProperty("resolvedBuildId").GetString());
        Assert.Equal(atlas.IndexIdB, root.GetProperty("build").GetProperty("indexId").GetString());
        Assert.Contains(root.GetProperty("provenance").EnumerateArray(), entry =>
            entry.GetProperty("classification").GetString() == "Fact");
    }

    [Fact]
    public async Task ExercisingEveryTool_MutatesNoAtlasFile()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildWithScenesAsync();
        var before = FileTree.HashAll(atlas.DataRoot);

        await McpTestHost.ExerciseEveryToolAsync(atlas);

        var after = FileTree.HashAll(atlas.DataRoot);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task OnlyPreferredIntegrityVerifiedExtractionIsReturned()
    {
        await using var atlas = await McpTestAtlas.SeedPreferredVerifiedBuildWithNonAuthoritativeCandidatesAsync();

        var results = await McpTestHost.QueryEveryCodeToolAsync(atlas);

        Assert.All(results, result =>
        {
            Assert.True(result.Status is ToolStatus.Resolved or ToolStatus.Ambiguous);
            Assert.Equal(atlas.IndexId, result.Build!.IndexId);
            Assert.True(result.Build.IntegrityVerified);
            Assert.NotEmpty(result.AnswerIndexIds);
            Assert.All(result.AnswerIndexIds, indexId => Assert.Equal(atlas.IndexId, indexId));
            Assert.DoesNotContain(result.Provenance, entry =>
                entry.ExtractionId == atlas.NonAuthoritativeExtractionId ||
                entry.IndexId == atlas.NonAuthoritativeIndexId);
        });
    }

    [Fact]
    public async Task CorruptedIndexedSource_ReturnsSourceIntegrityFailure()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        await File.WriteAllTextAsync(atlas.SourcePath, "tampered", CancellationToken.None);

        var envelope = await McpTestHost.GetSourceAsync(atlas);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("SourceIntegrityFailure", envelope.Error!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task ReadOnlyOpen_DoesNotCreateOrMigrate()
    {
        await using var atlas = await McpTestAtlas.CreateAbsentDatabaseRootAsync();

        var envelope = await McpTestHost.SearchSymbolsAsync(atlas.DataRoot, "Dealer");

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("AtlasUnavailable", envelope.Error!.Code);
        Assert.False(File.Exists(Path.Combine(atlas.DataRoot, "atlas.db")));
    }

    [Fact]
    public async Task ReadOnlyOpen_DirectToolsReturnAtlasUnavailableWithoutCreatingDatabase()
    {
        await using var atlas = await McpTestAtlas.CreateAbsentDatabaseRootAsync();

        var (builds, environment, comparison) = await McpTestHost.QueryDirectToolsAgainstAbsentDatabaseAsync(atlas.DataRoot);

        Assert.All([builds, environment, comparison], envelope =>
        {
            Assert.Equal(ToolStatus.Unavailable, envelope.Status);
            Assert.Equal("AtlasUnavailable", envelope.Error!.Code);
        });
        Assert.False(File.Exists(Path.Combine(atlas.DataRoot, "atlas.db")));
    }

    [Fact]
    public async Task NonAuthoritativeSceneSnapshot_IsRejectedBeforeQuerying()
    {
        await using var atlas = await McpTestAtlas.SeedPreferredVerifiedBuildWithNonAuthoritativeSceneSnapshotAsync();

        var envelope = await McpTestHost.GetSceneAsync(atlas, atlas.NonAuthoritativeSceneSnapshotId);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("SceneSnapshotNotFound", envelope.Error!.Code);
        Assert.Null(envelope.Data);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
        Assert.DoesNotContain(envelope.Provenance, entry =>
            entry.ExtractionId == atlas.NonAuthoritativeExtractionId ||
            entry.IndexId == atlas.NonAuthoritativeIndexId);
    }

    [Fact]
    public async Task DefaultAndExplicitHistoricalBuildResolve()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();

        var (defaultResult, historicalResult) = await McpTestHost.ResolveDefaultAndHistoricalBuildAsync(atlas);

        Assert.Equal(ToolStatus.Resolved, defaultResult.Status);
        Assert.Equal(atlas.BuildIdB, defaultResult.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdB, defaultResult.Build.IndexId);
        Assert.Equal(ToolStatus.Resolved, historicalResult.Status);
        Assert.Equal(atlas.BuildIdA, historicalResult.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, historicalResult.Build.IndexId);
    }

    [Fact]
    public async Task MissingAmbiguousAndUnavailableQueries_ReturnExplicitStatuses()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();

        var (missing, ambiguous, unavailable) = await McpTestHost.QueryExplicitFailureStatesAsync(atlas);

        Assert.Equal(ToolStatus.NotFound, missing.Status);
        Assert.Equal("SymbolNotFound", missing.Error!.Code);
        Assert.Equal(ToolStatus.Ambiguous, ambiguous.Status);
        Assert.NotEmpty(ambiguous.Candidates);
        Assert.Equal(ToolStatus.Unavailable, unavailable.Status);
        Assert.Equal("NoCurrentBuild", unavailable.Error!.Code);
    }
}

internal static class FileTree
{
    public static IReadOnlyDictionary<string, string> HashAll(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);
}

internal static class McpTestHost
{
    public static async Task<IReadOnlyList<string>> ListToolsThroughStdioAsync(string dataRoot)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(McpToolCatalog).Assembly.Location, "mcp", "serve"],
            EnvironmentVariables = new Dictionary<string, string?> { ["S1ATLAS_HOME"] = dataRoot },
            Name = "s1atlas-mcp-test"
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return tools.Select(tool => tool.Name).ToArray();
    }

    public static string ReadHostWiringSources() =>
        string.Concat(
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "Program.cs")),
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "McpServerComposition.cs")),
            File.ReadAllText(GetRepoPath("src", "S1Atlas.Mcp", "S1Atlas.Mcp.csproj")));

    public static async Task<string> CallSearchSymbolsThroughStdioAsync(string dataRoot)
    {
        await using var client = await CreateStdioClientAsync(dataRoot);
        var result = await client.CallToolAsync(
            "search_symbols",
            new Dictionary<string, object?>
            {
                ["query"] = "Dealer",
                ["buildId"] = null,
                ["kind"] = null,
                ["limit"] = 50
            },
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result.Content));
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    public static async Task ExerciseEveryToolAsync(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        var code = new CodeSymbolTools(services);
        var compare = new CompareTools(services);
        var build = new BuildEnvironmentTools(services);
        var scene = new SceneTools(services);
        var ct = CancellationToken.None;

        await code.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, ct);
        await code.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, "not-a-kind", 50, ct);
        await code.GetTypeAsync(atlas.TypeSelector, null, ct);
        await code.GetTypeAsync(" ", null, ct);
        await code.GetMethodAsync(atlas.MethodSelector, null, ct);
        await code.GetMethodAsync(" ", null, ct);
        await code.GetSourceAsync(atlas.MethodSelector, null, 0, ct);
        await code.GetSourceAsync(" ", null, 0, ct);
        await code.FindCallersAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindCallersAsync(" ", null, 50, ct);
        await code.FindReferencesAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindReferencesAsync(" ", null, 50, ct);
        await code.FindRelatedTypesAsync(atlas.MethodSelector, null, 50, ct);
        await code.FindRelatedTypesAsync(" ", null, 50, ct);

        await compare.CompareSymbolAsync(atlas.CompareSelector, atlas.BuildIdA, atlas.BuildIdB, ct);
        await compare.CompareSymbolAsync(atlas.CompareSelector, atlas.BuildIdA, " ", ct);
        await build.ListBuildsAsync(50, ct);
        await build.ListBuildsAsync(0, ct);
        await build.GetEnvironmentAsync(null, ct);
        await build.GetEnvironmentAsync("missing-build", ct);

        await scene.ListScenesAsync(atlas.BuildIdA, null, null, null, 50, ct);
        await scene.ListScenesAsync(atlas.BuildIdA, null, "not-a-kind", null, 50, ct);
        await scene.GetSceneAsync(atlas.SceneNameA, atlas.BuildIdA, null, null, false, false, false, 50, ct);
        await scene.GetSceneAsync(" ", atlas.BuildIdA, null, null, false, false, false, 50, ct);
        await scene.GetGameObjectAsync(atlas.GameObjectSelector, atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetGameObjectAsync(" ", atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetPrefabAsync(atlas.PrefabSelector, atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetPrefabAsync(" ", atlas.BuildIdA, null, false, false, false, 50, ct);
        await scene.GetComponentAsync(atlas.ComponentSelector, atlas.BuildIdA, null, false, true, 50, ct);
        await scene.GetComponentAsync(" ", atlas.BuildIdA, null, false, true, 50, ct);
    }

    public static async Task<IReadOnlyList<ToolObservation>> QueryEveryCodeToolAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        var ct = CancellationToken.None;
        var search = await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, ct);
        var type = await tools.GetTypeAsync(atlas.TypeSelector, null, ct);
        var method = await tools.GetMethodAsync(atlas.MethodSelector, null, ct);
        var source = await tools.GetSourceAsync(atlas.MethodSelector, null, 0, ct);
        var callers = await tools.FindCallersAsync(atlas.MethodSelector, null, 50, ct);
        var references = await tools.FindReferencesAsync(atlas.MethodSelector, null, 50, ct);
        var relatedTypes = await tools.FindRelatedTypesAsync(atlas.MethodSelector, null, 50, ct);
        return
        [
            Observe(search, search.Data!.Results.Select(result => result.IndexId)),
            Observe(type, SymbolIndexIds(type)),
            Observe(method, SymbolIndexIds(method)),
            Observe(source, [source.Build!.IndexId!]),
            Observe(callers, [callers.Data!.Resolution.Symbol!.IndexId]),
            Observe(references, [references.Data!.Resolution.Symbol!.IndexId]),
            Observe(relatedTypes, [relatedTypes.Data!.Resolution.Symbol!.IndexId])
        ];
    }

    public static Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(McpTestAtlas atlas) =>
        new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot))
            .GetSourceAsync(atlas.MethodSelector, null, 0, CancellationToken.None);

    public static Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(string dataRoot, string query) =>
        new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(dataRoot))
            .SearchSymbolsAsync(query, null, null, 50, CancellationToken.None);

    public static async Task<(ToolObservation Builds, ToolObservation Environment, ToolObservation Comparison)> QueryDirectToolsAgainstAbsentDatabaseAsync(string dataRoot)
    {
        var services = McpServerComposition.BuildReadOnlyServices(dataRoot);
        var builds = new BuildEnvironmentTools(services);
        var compare = new CompareTools(services);
        return (
            Observe(await builds.ListBuildsAsync(50, CancellationToken.None)),
            Observe(await builds.GetEnvironmentAsync(null, CancellationToken.None)),
            Observe(await compare.CompareSymbolAsync("N.T.M()", "build-a", "build-b", CancellationToken.None)));
    }

    public static Task<ToolEnvelope<S1Atlas.Indexing.Scene.SceneDocumentQueryResult>> GetSceneAsync(
        McpTestAtlas atlas,
        string sceneSnapshotId) =>
        new SceneTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot))
            .GetSceneAsync(atlas.SceneNameA, atlas.BuildIdA, sceneSnapshotId, null, false, false, false, 50, CancellationToken.None);

    public static async Task<(ToolObservation Default, ToolObservation Historical)> ResolveDefaultAndHistoricalBuildAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        return (
            Observe(await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, null, null, 50, CancellationToken.None)),
            Observe(await tools.SearchSymbolsAsync(atlas.KnownSymbolFragment, atlas.BuildIdA, null, 50, CancellationToken.None)));
    }

    public static async Task<(ToolObservation Missing, ToolObservation Ambiguous, ToolObservation Unavailable)> QueryExplicitFailureStatesAsync(McpTestAtlas atlas)
    {
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        await using var empty = await McpTestAtlas.EmptyAsync();
        var emptyTools = new BuildEnvironmentTools(McpServerComposition.BuildReadOnlyServices(empty.DataRoot));
        return (
            Observe(await tools.GetTypeAsync("Missing.Symbol", null, CancellationToken.None)),
            Observe(await tools.GetTypeAsync("DealerService", null, CancellationToken.None)),
            Observe(await emptyTools.GetEnvironmentAsync(null, CancellationToken.None)));
    }

    private static IReadOnlyList<string> SymbolIndexIds(ToolEnvelope<SymbolQueryResult> envelope) =>
        envelope.Data is not null
            ? [envelope.Data.IndexId]
            : envelope.Candidates.Cast<SymbolQueryResult>().Select(candidate => candidate.IndexId).ToArray();

    private static ToolObservation Observe<T>(ToolEnvelope<T> envelope) where T : class =>
        Observe(envelope, []);

    private static ToolObservation Observe<T>(ToolEnvelope<T> envelope, IEnumerable<string> answerIndexIds) where T : class =>
        new(envelope.Status, envelope.Build, envelope.Candidates, envelope.Provenance, envelope.Error, answerIndexIds.ToArray());

    private static async Task<McpClient> CreateStdioClientAsync(string dataRoot)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(McpToolCatalog).Assembly.Location, "mcp", "serve"],
            EnvironmentVariables = new Dictionary<string, string?> { ["S1ATLAS_HOME"] = dataRoot },
            Name = "s1atlas-mcp-test"
        });
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static string GetRepoPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }
}

internal sealed record ToolObservation(
    ToolStatus Status,
    BuildContext? Build,
    IReadOnlyList<object> Candidates,
    IReadOnlyList<ProvenanceEntry> Provenance,
    ToolError? Error,
    IReadOnlyList<string> AnswerIndexIds);
