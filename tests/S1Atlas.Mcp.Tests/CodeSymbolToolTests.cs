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
    public void GetSource_AppendsOptionalArgumentsAfterScopeAndCollection()
    {
        var parameters = typeof(CodeSymbolTools)
            .GetMethod(nameof(CodeSymbolTools.GetSourceAsync))!
            .GetParameters();

        Assert.Equal(
            ["selector", "buildId", "context", "ct", "scope", "collection", "fullType", "relatedLimit"],
            parameters.Select(parameter => parameter.Name));
        Assert.Equal(false, parameters[^2].DefaultValue);
        Assert.Equal(10, parameters[^1].DefaultValue);
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
    public async Task GetSource_DefaultsToBoundedCallableNeighborhood()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            ct: CancellationToken.None);

        var neighborhood = envelope.Data!.Neighborhood;
        Assert.NotNull(neighborhood);
        Assert.Equal(1, neighborhood.CallerTotal);
        Assert.Equal(1, neighborhood.CalleeTotal);
        Assert.Single(neighborhood.Callers);
        Assert.Single(neighborhood.Callees);
        Assert.Empty(neighborhood.References);
        Assert.Equal(0, neighborhood.ReferenceTotal);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
    }

    [Fact]
    public async Task GetSource_RelatedLimitZero_OmitsNeighborhood()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            ct: CancellationToken.None,
            relatedLimit: 0);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Null(envelope.Data!.Neighborhood);
        Assert.Null(envelope.Data.NeighborhoodNotice);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task GetSource_InvalidRelatedLimit_ReturnsSelectedBuild(int relatedLimit)
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            ct: CancellationToken.None,
            relatedLimit: relatedLimit);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidRelatedLimit", envelope.Error!.Code);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
    }

    [Fact]
    public async Task GetSource_FullType_ReturnsContainingTypeWithoutNeighborhood()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            ct: CancellationToken.None,
            fullType: true);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("type-widget", envelope.Data!.Symbol.SymbolId);
        Assert.Contains("public class Widget", envelope.Data.Text, StringComparison.Ordinal);
        Assert.Null(envelope.Data.Neighborhood);
        Assert.Null(envelope.Data.NeighborhoodNotice);
        Assert.Equal(atlas.IndexId, envelope.Data.IndexId);
    }

    [Fact]
    public async Task GetSource_ExposesStaticRuntimeVerificationHint()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.RuntimeMethodSelector,
            buildId: null,
            context: 0,
            ct: CancellationToken.None,
            relatedLimit: 0);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.NotNull(envelope.Data!.RuntimeVerification);
        var hint = envelope.Data.RuntimeVerification!;
        Assert.Equal([RuntimeVerificationSignal.Physics], hint.Signals);
        Assert.Contains("in-game", hint.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(envelope.Data.Neighborhood);
    }

    [Fact]
    public async Task GetSource_ReferenceScope_UsesPinnedCollectionAndFederatedNeighborhood()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        await atlas.AddReferenceSourceLocationAsync(reference);
        var tools = CreateTools(atlas);
        var selected = await tools.SearchSymbolsAsync(
            "Qol.Mod::Run",
            buildId: null,
            kind: null,
            limit: 50,
            ct: CancellationToken.None,
            scope: "reference",
            collection: reference.Collection);

        var source = await tools.GetSourceAsync(
            selected.Data!.Results[0].Signature,
            buildId: null,
            context: 0,
            ct: CancellationToken.None,
            scope: "reference",
            collection: reference.Collection,
            relatedLimit: 1);

        Assert.Equal(ToolStatus.Resolved, source.Status);
        Assert.Equal(reference.IndexId, source.Data!.IndexId);
        Assert.Equal("reference", source.Data.Origin);
        Assert.Equal(1, source.Data.Neighborhood!.CalleeTotal);
        Assert.Single(source.Data.Neighborhood.Callees);
        Assert.Equal("game", source.Data.Neighborhood.Callees[0].Target.Origin);
        Assert.Contains(source.Provenance, entry => entry.Source == "reference-collection");
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
    public async Task FindCallSites_ReturnsBoundedRecoveredIlEvidenceWithoutClaimingRuntimeOrder()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindCallSitesAsync(
            atlas.EngineCallSiteSelector,
            buildId: null,
            limit: 1,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdValue, envelope.Build?.ResolvedBuildId);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
        Assert.Equal(3, envelope.Data!.TotalCount);
        Assert.Equal(1, envelope.Data.ReturnedCount);
        var relationship = Assert.Single(envelope.Data.Relationships);
        Assert.Equal("callsite-001", relationship.RelationshipId);
        Assert.True(relationship.Source.Resolved);
        Assert.False(relationship.Target.Resolved);
        Assert.Equal(atlas.EngineCallSiteTargetText, relationship.Target.RawText);
        Assert.Contains("recovered IL references", envelope.Data.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not prove runtime behavior", envelope.Data.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution order", envelope.Data.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.All(envelope.Provenance, entry => Assert.NotEqual(ProvenanceClassification.Interpretation, entry.Classification));
    }

    [Fact]
    public async Task FindCallSites_ReferenceScope_PreservesCollectionBindingAndProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedTargetQueryReferenceCollectionAsync("qol-targets");
        var tools = CreateTools(atlas);

        var envelope = await tools.FindCallSitesAsync(
            atlas.EngineCallSiteSelector,
            buildId: null,
            limit: 50,
            ct: CancellationToken.None,
            scope: "reference",
            collection: reference.Collection);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdA, envelope.Build?.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, envelope.Build?.IndexId);
        var collectionProvenance = Assert.Single(envelope.Provenance, entry => entry.Source == "reference-collection");
        Assert.Equal(reference.IndexId, collectionProvenance.IndexId);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal("reference-callsite-qol-targets", relationship.RelationshipId);
        Assert.Equal("reference", relationship.Source.Origin);
        Assert.Equal(reference.Collection, relationship.Source.Collection);
        Assert.Equal("qol", relationship.Source.ReferenceModId);
        Assert.False(relationship.Target.Resolved);
        Assert.Equal(atlas.EngineCallSiteTargetText, relationship.Target.RawText);
    }

    [Fact]
    public async Task FindFieldReferences_SeparatesReaderAndWriterFilters()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var readers = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector,
            buildId: null,
            readers: true,
            writers: false,
            limit: 50,
            ct: CancellationToken.None);
        var writers = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector,
            buildId: null,
            readers: false,
            writers: true,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, readers.Status);
        Assert.Equal(["reads-widget-field"], readers.Data!.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(readers.Data.Relationships, edge => Assert.Equal("ReadsField", edge.Kind));
        Assert.Contains("recovered IL references", readers.Data.CompletenessNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lifecycle ordering", readers.Data.CompletenessNotice, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ToolStatus.Resolved, writers.Status);
        Assert.Equal(["writes-widget-field"], writers.Data!.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(writers.Data.Relationships, edge => Assert.Equal("WritesField", edge.Kind));
    }

    [Fact]
    public async Task FindFieldReferences_RespectsBoundedLimit()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector,
            buildId: null,
            readers: false,
            writers: false,
            limit: 1,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(2, envelope.Data!.TotalCount);
        Assert.Equal(1, envelope.Data.ReturnedCount);
        Assert.Single(envelope.Data.Relationships);
    }

    [Fact]
    public async Task FindFieldReferences_InvalidLimit_ReturnsSelectedBuild()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector,
            buildId: null,
            readers: false,
            writers: false,
            limit: 0,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidLimit", envelope.Error?.Code);
        Assert.Equal(atlas.IndexId, envelope.Build?.IndexId);
    }

    [Fact]
    public async Task FindFieldReferences_ReferenceScope_PreservesCollectionBindingAndTargetProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedTargetQueryReferenceCollectionAsync("qol-targets");
        var tools = CreateTools(atlas);

        var envelope = await tools.FindFieldReferencesAsync(
            atlas.ReferenceFieldSelector,
            buildId: null,
            readers: false,
            writers: true,
            limit: 50,
            ct: CancellationToken.None,
            scope: "reference",
            collection: reference.Collection);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdA, envelope.Build?.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, envelope.Build?.IndexId);
        var collectionProvenance = Assert.Single(envelope.Provenance, entry => entry.Source == "reference-collection");
        Assert.Equal(reference.IndexId, collectionProvenance.IndexId);
        Assert.Equal("reference", envelope.Data!.Resolution.Symbol!.Origin);
        Assert.Equal(reference.Collection, envelope.Data.Resolution.Symbol.Collection);
        var relationship = Assert.Single(envelope.Data.Relationships);
        Assert.Equal("reference-field-write-qol-targets", relationship.RelationshipId);
        Assert.Equal("WritesField", relationship.Kind);
        Assert.Equal("reference", relationship.Source.Origin);
        Assert.Equal(reference.Collection, relationship.Source.Collection);
        Assert.Equal("reference", relationship.Target.Origin);
        Assert.Equal(reference.Collection, relationship.Target.Collection);
    }

    [Fact]
    public async Task FindFieldReferences_AmbiguousSelector_ReturnsCandidatesWithoutRelationships()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindFieldReferencesAsync(
            atlas.AmbiguousFieldSelector,
            buildId: null,
            readers: false,
            writers: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.Equal(2, envelope.Candidates.Count);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task FindFieldReferences_RejectsMutuallyExclusiveDirectionFlags()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector,
            buildId: null,
            readers: true,
            writers: true,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidOptionCombination", envelope.Error?.Code);
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
