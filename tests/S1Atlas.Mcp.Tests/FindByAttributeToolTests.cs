using System.Text.Json;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;
using S1Atlas.Core.Storage;
using S1Atlas.Mcp.Tools;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class FindByAttributeToolTests
{
    [Fact]
    public void FindByAttribute_IsRegisteredForDiscovery()
    {
        Assert.Contains("find_by_attribute", McpToolCatalog.DiscoverToolNames());
    }

    [Fact]
    public async Task FindByAttribute_FullName_ReturnsMatchingSymbolsWithDecodedArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.FindByAttributeAsync(
            "FishNet.Object.ServerRpcAttribute",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdValue, envelope.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexId, envelope.Build.IndexId);
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Fact);
        Assert.Equal(1, envelope.Data!.TotalCount);
        Assert.Equal(1, envelope.Data.ReturnedCount);
        var hit = Assert.Single(envelope.Data.Results);
        Assert.Equal("Demo.Widget.Run", hit.Symbol.QualifiedName);
        var attribute = Assert.Single(hit.MatchingAttributes);
        Assert.Equal("FishNet.Object.ServerRpcAttribute", attribute.AttributeType);
        Assert.Equal("Member", attribute.Target);
        Assert.Null(attribute.ParameterIndex);
        Assert.Null(attribute.ParameterName);
        Assert.Equal(42, Assert.Single(attribute.FixedArguments).Value);
        var named = Assert.Single(attribute.NamedArguments);
        Assert.Equal("RequireOwnership", named.Name);
        Assert.Equal(false, named.Value.Value);
    }

    [Fact]
    public async Task FindByAttribute_Suffix_MatchesAfterLastDot()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.FindByAttributeAsync(
            "ServerRpcAttribute",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(2, envelope.Data!.TotalCount);
        Assert.Equal(2, envelope.Data.ReturnedCount);
        Assert.Equal(
            ["Demo.Service.Execute", "Demo.Widget.Run"],
            envelope.Data.Results.Select(hit => hit.Symbol.QualifiedName));
        Assert.Equal(
            ["Other.ServerRpcAttribute", "FishNet.Object.ServerRpcAttribute"],
            envelope.Data.Results.Select(hit => Assert.Single(hit.MatchingAttributes).AttributeType));
    }

    [Fact]
    public async Task FindByAttribute_NamedArgumentFilter_MatchesNameAndCanonicalValue()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.FindByAttributeAsync(
            "FishNet.Object.ServerRpcAttribute",
            ["RequireOwnership=false"],
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("Demo.Widget.Run", Assert.Single(envelope.Data!.Results).Symbol.QualifiedName);
    }

    [Fact]
    public async Task FindByAttribute_BareValueFilter_MatchesAnyFixedArgument()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var matching = await tools.FindByAttributeAsync(
            "FishNet.Object.ServerRpcAttribute",
            ["42"],
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);
        var missing = await tools.FindByAttributeAsync(
            "FishNet.Object.ServerRpcAttribute",
            ["7"],
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, matching.Status);
        Assert.Equal("Demo.Widget.Run", Assert.Single(matching.Data!.Results).Symbol.QualifiedName);
        Assert.Equal(ToolStatus.NotFound, missing.Status);
        Assert.Equal("SymbolNotFound", missing.Error!.Code);
    }

    [Fact]
    public async Task FindByAttribute_ConflictingFilters_CombineWithAnd()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.FindByAttributeAsync(
            "FishNet.Object.ServerRpcAttribute",
            ["RequireOwnership=false", "7"],
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("SymbolNotFound", envelope.Error!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task FindByAttribute_UnknownAttribute_ReturnsNotFound()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.FindByAttributeAsync(
            "Does.Not.ExistAttribute",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("SymbolNotFound", envelope.Error!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task FindByAttribute_InjectedAnalysisNames_ReturnNoResults()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var attribute in new[] { "Cpp2ILInjected.AddressAttribute", "Cpp2ILInjected.TokenAttribute" })
        {
            var envelope = await tools.FindByAttributeAsync(
                attribute,
                codebase: McpCodebase.scheduleI,
                channel: CodeChannel.Installed,
                buildId: null,
                limit: 10,
                ct: cancellationToken);

            Assert.Equal(ToolStatus.NotFound, envelope.Status);
            Assert.Equal("SymbolNotFound", envelope.Error!.Code);
        }
    }

    [Fact]
    public async Task FindByAttribute_Limit_RejectsNonPositiveAndCapsResults()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var invalid = await tools.FindByAttributeAsync(
            "ServerRpcAttribute",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 0,
            ct: cancellationToken);
        var capped = await tools.FindByAttributeAsync(
            "ServerRpcAttribute",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 1,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Invalid, invalid.Status);
        Assert.Equal("InvalidLimit", invalid.Error!.Code);
        Assert.Equal(ToolStatus.Resolved, capped.Status);
        Assert.Equal(2, capped.Data!.TotalCount);
        Assert.Equal(1, capped.Data.ReturnedCount);
        Assert.Equal("Demo.Service.Execute", Assert.Single(capped.Data.Results).Symbol.QualifiedName);
    }

    [Fact]
    public async Task FindByAttribute_BlankAttribute_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var game = await tools.FindByAttributeAsync(
            "   ",
            codebase: McpCodebase.scheduleI,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);
        var api = await tools.FindByAttributeAsync(
            "   ",
            codebase: McpCodebase.s1api,
            channel: CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Invalid, game.Status);
        Assert.Equal("InvalidArguments", game.Error!.Code);
        Assert.Equal(atlas.BuildIdValue, game.Build!.ResolvedBuildId);
        Assert.Equal(ToolStatus.Invalid, api.Status);
        Assert.Equal("InvalidArguments", api.Error!.Code);
    }

    [Fact]
    public async Task GetType_IncludesSymbolAttributes()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.GetTypeAsync(
            atlas.TypeSelector,
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var attribute = Assert.Single(envelope.Data!.Attributes!);
        Assert.Equal("System.ObsoleteAttribute", attribute.AttributeType);
        Assert.Equal("Type", attribute.Target);
        Assert.Equal("Use Demo.Widget2", Assert.Single(attribute.FixedArguments).Value);
        Assert.Empty(attribute.NamedArguments);
    }

    [Fact]
    public async Task GetMethod_IncludesMemberAndParameterAttributes()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var envelope = await tools.GetMethodAsync(
            atlas.MethodSelector,
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(2, envelope.Data!.Attributes!.Count);
        var member = Assert.Single(
            envelope.Data.Attributes,
            attribute => attribute.AttributeType == "FishNet.Object.ServerRpcAttribute");
        Assert.Equal("Member", member.Target);
        Assert.Equal(42, Assert.Single(member.FixedArguments).Value);
        Assert.Equal("RequireOwnership", Assert.Single(member.NamedArguments).Name);
        var parameter = Assert.Single(
            envelope.Data.Attributes,
            attribute => attribute.AttributeType == "Demo.Annotations.ValidateAttribute");
        Assert.Equal("Parameter", parameter.Target);
        Assert.Equal(0, parameter.ParameterIndex);
        Assert.Equal("payload", parameter.ParameterName);
    }

    [Fact]
    public async Task GetTypeAndMethod_WithoutAttributeRows_ReturnEmptyAttributes()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);
        var cancellationToken = TestContext.Current.CancellationToken;

        var type = await tools.GetTypeAsync(
            atlas.TypeSelector,
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            ct: cancellationToken);
        var method = await tools.GetMethodAsync(
            atlas.MethodSelector,
            McpCodebase.scheduleI,
            CodeChannel.Installed,
            buildId: null,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, type.Status);
        Assert.NotNull(type.Data!.Attributes);
        Assert.Empty(type.Data.Attributes);
        Assert.Equal(ToolStatus.Resolved, method.Status);
        Assert.NotNull(method.Data!.Attributes);
        Assert.Empty(method.Data.Attributes);
    }

    [Fact]
    public async Task FindByAttribute_ApiInstalled_ResolvesFromApiIndex()
    {
        await using var atlas = await ApiAttributeAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedInstalledApiIndexAsync(cancellationToken);

        var envelope = await atlas.Tools.FindByAttributeAsync(
            "ObsoleteAttribute",
            ["Use New instead"],
            McpCodebase.s1api,
            CodeChannel.Installed,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("S1Api", envelope.Build!.Codebase);
        Assert.Equal("Installed", envelope.Build.Channel);
        Assert.Equal(ApiAttributeAtlas.InstalledIndexId, envelope.Build.IndexId);
        Assert.Contains(
            envelope.Provenance,
            entry => entry.Classification == ProvenanceClassification.Fact &&
                entry.Source!.Contains("api-index:S1Api:Installed", StringComparison.Ordinal));
        var hit = Assert.Single(envelope.Data!.Results);
        Assert.Equal("Demo.Api.Legacy", hit.Symbol.QualifiedName);
        var attribute = Assert.Single(hit.MatchingAttributes);
        Assert.Equal("System.ObsoleteAttribute", attribute.AttributeType);
        Assert.Equal("Use New instead", Assert.Single(attribute.FixedArguments).Value);
    }

    [Fact]
    public async Task GetMethod_ApiInstalled_IncludesSymbolAttributes()
    {
        await using var atlas = await ApiAttributeAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedInstalledApiIndexAsync(cancellationToken);

        var envelope = await atlas.Tools.GetMethodAsync(
            "Demo.Api.Legacy",
            McpCodebase.s1api,
            CodeChannel.Installed,
            buildId: null,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var attribute = Assert.Single(envelope.Data!.Attributes!);
        Assert.Equal("System.ObsoleteAttribute", attribute.AttributeType);
        Assert.Equal("Use New instead", Assert.Single(attribute.FixedArguments).Value);
    }

    [Fact]
    public async Task FindByAttribute_ApiRelease_CarriesNoAttributes()
    {
        await using var atlas = await ApiAttributeAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedReleaseApiIndexAsync(cancellationToken);

        var envelope = await atlas.Tools.FindByAttributeAsync(
            "ObsoleteAttribute",
            codebase: McpCodebase.s1api,
            channel: CodeChannel.Release,
            buildId: null,
            limit: 10,
            ct: cancellationToken);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("SymbolNotFound", envelope.Error!.Code);
    }

    [Fact]
    public async Task FindByAttribute_Stdio_SerializesAttributeHitsOnTheWire()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeAttributes: true);
        await using var server = await McpTestServer.StartAsync(
            atlas.DataRoot,
            TestContext.Current.CancellationToken);

        var serialized = await McpTestHost.CallToolAsync(
            server.Client,
            "find_by_attribute",
            new Dictionary<string, object?>
            {
                ["codebase"] = "scheduleI",
                ["attribute"] = "ServerRpcAttribute",
                ["limit"] = 10
            });

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        Assert.Equal("resolved", root.GetProperty("status").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        var results = data.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        var run = Assert.Single(results, hit =>
            hit.GetProperty("symbol").GetProperty("qualifiedName").GetString() == "Demo.Widget.Run");
        var matching = Assert.Single(run.GetProperty("matchingAttributes").EnumerateArray());
        Assert.Equal("FishNet.Object.ServerRpcAttribute", matching.GetProperty("attributeType").GetString());
        Assert.Equal("Member", matching.GetProperty("target").GetString());
        var fixedArgument = Assert.Single(matching.GetProperty("fixedArguments").EnumerateArray());
        Assert.Equal("Primitive", fixedArgument.GetProperty("kind").GetString());
        Assert.Equal(42, fixedArgument.GetProperty("value").GetInt32());
        var namedArgument = Assert.Single(matching.GetProperty("namedArguments").EnumerateArray());
        Assert.Equal("RequireOwnership", namedArgument.GetProperty("name").GetString());
        Assert.False(namedArgument.GetProperty("value").GetProperty("value").GetBoolean());
    }

    private static CodeSymbolTools CreateTools(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        return new CodeSymbolTools(services);
    }

    private sealed class ApiAttributeAtlas : IAsyncDisposable
    {
        public const string InstalledIndexId = "api-attribute-installed";
        public const string ReleaseIndexId = "api-attribute-release";

        private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-30T00:00:00Z");

        private readonly SqliteAtlasRepository _repository;
        private int _seedOrdinal;

        private ApiAttributeAtlas(string root)
        {
            Root = root;
            _repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"), Path.Combine(root, "backups"));
        }

        public string Root { get; }

        public CodeSymbolTools Tools => new(McpServerComposition.BuildReadOnlyServices(Root));

        public static async Task<ApiAttributeAtlas> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-attribute-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var atlas = new ApiAttributeAtlas(root);
            await atlas._repository.InitializeAsync(TestContext.Current.CancellationToken);
            return atlas;
        }

        public async Task SeedInstalledApiIndexAsync(CancellationToken cancellationToken)
        {
            var environmentId = await SeedCurrentBuildAsync(cancellationToken);
            var snapshotId = "snapshot-" + InstalledIndexId;
            var createdAt = BaseTime.AddMinutes(_seedOrdinal++).ToString("O");
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(
                    snapshotId,
                    CodebaseKind.S1Api,
                    CodeChannel.Installed,
                    "installed-s1api-binary",
                    createdAt,
                    environmentId),
                cancellationToken);
            await _repository.StartIndexRunAsync(
                new IndexRunRecord(InstalledIndexId, snapshotId, IndexRunStatus.Running, createdAt),
                cancellationToken);
            await _repository.CompleteIndexRunAsync(
                InstalledIndexId,
                new IndexWriteSet(
                    [ApiSymbol("api-legacy", CodebaseKind.S1Api, CodeChannel.Installed, "Demo.Api.Legacy") with { SnapshotId = snapshotId }],
                    [],
                    [],
                    [],
                    [],
                    CustomAttributes:
                    [
                        new IndexCustomAttributeRecord(
                            "attr-api-001",
                            snapshotId,
                            "api-legacy",
                            "Member",
                            null,
                            null,
                            "System.ObsoleteAttribute",
                            CustomAttributeJson.ToJson(new DecodedCustomAttribute(
                                "System.ObsoleteAttribute",
                                [new CustomAttributeValue(CustomAttributeValueKind.String, "Use New instead", "System.String", null)],
                                [])))
                    ]),
                BaseTime.AddMinutes(_seedOrdinal++).ToString("O"),
                cancellationToken);
        }

        public async Task SeedReleaseApiIndexAsync(CancellationToken cancellationToken)
        {
            var snapshotId = "snapshot-" + ReleaseIndexId;
            var createdAt = BaseTime.AddMinutes(_seedOrdinal++).ToString("O");
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(
                    snapshotId,
                    CodebaseKind.S1Api,
                    CodeChannel.Release,
                    new string('c', 40),
                    createdAt,
                    null),
                cancellationToken);
            await _repository.StartIndexRunAsync(
                new IndexRunRecord(ReleaseIndexId, snapshotId, IndexRunStatus.Running, createdAt),
                cancellationToken);
            await _repository.CompleteIndexRunAsync(
                ReleaseIndexId,
                new IndexWriteSet(
                    [ApiSymbol("api-release-candidate", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Api.ReleaseCandidate") with { SnapshotId = snapshotId }],
                    [],
                    [],
                    [],
                    []),
                BaseTime.AddMinutes(_seedOrdinal++).ToString("O"),
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await TestDirectory.DeleteTreeAsync(Root);
        }

        private async Task<string> SeedCurrentBuildAsync(CancellationToken cancellationToken)
        {
            const string buildId = "build-current";
            var capturedAt = BaseTime.AddMinutes(_seedOrdinal++);
            var snapshot = new EnvironmentSnapshot(
                2,
                new GameBuild(
                    buildId,
                    new string('1', 64),
                    new string('2', 64),
                    capturedAt,
                    true),
                new InstallationObservation("fixture", "app", buildId, Root, null, null),
                [new DependencyVersion(DependencyKind.S1Api, "3.0.0", "S1API.dll", true, "installed-s1api-binary")],
                "test",
                capturedAt);
            await _repository.SaveSnapshotAsync(snapshot, cancellationToken);
            return EnvironmentSnapshotId.Create(snapshot);
        }

        private static IndexSymbolRecord ApiSymbol(
            string symbolId,
            CodebaseKind codebase,
            CodeChannel channel,
            string qualifiedName) =>
            new(
                symbolId,
                string.Empty,
                $"{codebase}:{channel}:Method:{qualifiedName}:void",
                "Method",
                qualifiedName,
                $"System.Void {qualifiedName}()",
                false,
                BodyRecoveryStatus.Recovered);
    }
}
