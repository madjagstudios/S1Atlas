using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Mcp.Mapping;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class CodeSymbolToolTests
{
    [Fact]
    public async Task GetType_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetTypeAsync(
            "   ",
            buildId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
        Assert.Equal(atlas.BuildIdValue, envelope.Build?.ResolvedBuildId);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
    }

    [Fact]
    public async Task GetMethod_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetMethodAsync(
            "   ",
            buildId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task GetCallableSurface_ReturnsResolvedLocalOnlyWrapperEvidence()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetCallableSurfaceAsync(
            atlas.MethodSelector,
            buildId: null,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(CallableSurfaceStatus.Resolved.ToString(), envelope.Data!.Status);
        Assert.Equal(CallableSurfaceKind.PublicMethodWrapper.ToString(), envelope.Data.Kind);
        Assert.Equal(InteropInputTrust.LocalOnly.ToString(), envelope.Data.InteropInputTrust);
        Assert.Contains("il2cpp_runtime_invoke", envelope.Data.Evidence, StringComparison.Ordinal);
        Assert.All(envelope.Provenance, entry => Assert.NotEqual(ProvenanceClassification.Interpretation, entry.Classification));
    }

    [Fact]
    public async Task GetCallableSurface_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetCallableSurfaceAsync(
            "   ",
            buildId: null,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task SearchSymbols_HealthyBuild_ResolvesAgainstPreferredIndex()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            atlas.KnownSymbolFragment,
            buildId: null,
            kind: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
        Assert.NotNull(envelope.Data);
        Assert.All(
            envelope.Provenance,
            entry => Assert.NotEqual(ProvenanceClassification.Interpretation, entry.Classification));
    }

    [Fact]
    public async Task SearchSymbols_NoCurrentBuild_ReturnsUnavailable()
    {
        await using var atlas = await McpTestAtlas.EmptyAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            "Dealer",
            buildId: null,
            kind: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("NoCurrentBuild", envelope.Error?.Code);
    }

    [Fact]
    public async Task SearchSymbols_BlankQuery_ReturnsInvalidArgumentsWithSelectedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            "   ",
            buildId: null,
            kind: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
        Assert.Equal(atlas.BuildIdValue, envelope.Build?.ResolvedBuildId);
    }

    [Fact]
    public async Task SearchSymbols_InvalidKind_ReturnsSelectedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            atlas.KnownSymbolFragment,
            buildId: null,
            kind: "not-a-kind",
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidKind", envelope.Error?.Code);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
    }

    [Fact]
    public async Task GetType_UnknownSelector_ReturnsNotFound()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetTypeAsync(
            "Demo.DoesNotExist",
            buildId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task GetMethod_AmbiguousSelector_ReturnsAmbiguousWithCandidates()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetMethodAsync(
            "worker",
            buildId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.True(envelope.Candidates.Count >= 2);
    }

    [Fact]
    public async Task GetMethod_LimitOne_StillReturnsAmbiguous()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetMethodAsync(
            "worker",
            buildId: null,
            limit: 1,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.Single(envelope.Candidates);
    }

    [Fact]
    public async Task GetType_MultipleMatches_WithLimitOne_StillReturnsAmbiguous()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetTypeAsync(
            "DealerService",
            buildId: null,
            limit: 1,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.Single(envelope.Candidates);
        Assert.Equal(atlas.BuildIdValue, envelope.Build!.ResolvedBuildId);
    }

    [Fact]
    public async Task UnexpectedServiceException_IsLoggedAndKeepsBuildContext()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        using var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var envelope = await EnvelopeMapper.WithAuthorityAsync<SymbolQueryResult>(
                services.AuthorityResolver,
                buildId: null,
                CancellationToken.None,
                _ => throw new InvalidOperationException("private source path must not escape"));

            Assert.Equal(ToolStatus.Unavailable, envelope.Status);
            Assert.Equal("UnexpectedToolFailure", envelope.Error?.Code);
            Assert.Equal(atlas.BuildIdValue, envelope.Build?.ResolvedBuildId);
            Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
            Assert.DoesNotContain("private source path", envelope.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("Unexpected MCP tool failure", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMethod_InvalidLimit_ReturnsSelectedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetMethodAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 0,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidLimit", envelope.Error?.Code);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
    }

    [Fact]
    public async Task GetSource_ReturnsHashVerifiedSnippet()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("public void Run() { }", envelope.Data!.Text);
        Assert.Equal(atlas.SourceRelativePath, envelope.Data.RelativePath);
    }

    [Fact]
    public async Task GetSource_PreservesStubOrUnavailableBodyStatus()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(
            methodBodyStatus: BodyRecoveryStatus.StubOrUnavailable);
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, envelope.Data!.BodyRecoveryStatus);
    }

    [Fact]
    public async Task GetSource_TamperedFile_ReturnsSourceIntegrityFailure()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);
        await File.WriteAllTextAsync(atlas.SourcePath, "tampered", CancellationToken.None);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("SourceIntegrityFailure", envelope.Error?.Code);
    }

    [Fact]
    public async Task GetSource_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            "   ",
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindCallers_PreservesCompletenessNotice()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindCallersAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.NotEmpty(envelope.Data!.CompletenessNotice);
        Assert.True(envelope.Data.CallerCompletenessBoundedByTargetResolution);
    }

    [Fact]
    public async Task FindCallers_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindCallersAsync(
            "   ",
            buildId: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindReferences_ReturnsIncomingAndOutgoingEdges()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindReferencesAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Contains(envelope.Data!.Relationships, edge => edge.RelationshipId == "incoming-call");
        Assert.Contains(envelope.Data.Relationships, edge => edge.RelationshipId == "outgoing-call");
        Assert.Contains(envelope.Data.Relationships, edge => edge.RelationshipId == "reads-widget-field");
    }

    [Fact]
    public async Task FindReferences_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindReferencesAsync(
            "   ",
            buildId: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindRelatedTypes_FiltersToTypeRelations()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindRelatedTypesAsync(
            atlas.MethodSelector,
            buildId: null,
            relationKinds: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(
            ["parameter-type-payload", "return-type-result"],
            envelope.Data!.Relationships.Select(edge => edge.RelationshipId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain(envelope.Data.Relationships, edge => edge.Kind == "Calls");
        Assert.DoesNotContain(envelope.Data.Relationships, edge => edge.Kind == "ReadsField");
    }

    [Fact]
    public async Task FindRelatedTypes_UsesRelationKindsAndLimitAfterFiltering()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindRelatedTypesAsync(
            atlas.MethodSelector,
            buildId: null,
            relationKinds: ["ReturnType"],
            limit: 1,
            ct: CancellationToken.None);

        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal("ReturnType", relationship.Kind);
        Assert.Equal("return-type-result", relationship.RelationshipId);
    }

    [Fact]
    public async Task FindRelatedTypes_InvalidRelationKind_ReturnsSelectedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindRelatedTypesAsync(
            atlas.MethodSelector,
            buildId: null,
            relationKinds: ["Calls"],
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidKind", envelope.Error?.Code);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
    }

    [Fact]
    public async Task FindRelatedTypes_BlankSelector_ReturnsInvalidArguments()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindRelatedTypesAsync(
            "   ",
            buildId: null,
            relationKinds: null,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    private static CodeSymbolTools CreateTools(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        return new CodeSymbolTools(services);
    }
}
