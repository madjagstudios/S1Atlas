using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Mapping;
using S1Atlas.Mcp.Tools;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class SeamToolTests
{
    [Fact]
    public async Task InvestigateSeam_IsRegisteredWithApprovedSchemaAndNoMutationVerbs()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateBareAsync();

        var toolNames = McpToolCatalog.DiscoverToolNames();

        Assert.Contains("investigate_seam", toolNames);
        Assert.All(
            toolNames.Where(name => name != "list_api_indexes"),
            name => Assert.DoesNotContain(
                ["extract", "promote", "cleanup", "install", "scan", "index", "sync", "delete", "write", "set"],
                verb => name.Contains(verb, StringComparison.OrdinalIgnoreCase)));

        var schemas = await McpTestHost.GetToolSchemasThroughStdioAsync(atlas.DataRoot);
        AssertSchema(
            schemas["investigate_seam"],
            ["behavioralQuestion", "selector", "buildId", "scope", "collection", "relationshipLimit", "ownerLimit", "context", "details", "nativeSymbolIds", "nativeTraversalBudget"],
            ["behavioralQuestion", "selector"]);

        using var schema = JsonDocument.Parse(schemas["investigate_seam"]);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(50, properties.GetProperty("relationshipLimit").GetProperty("default").GetInt32());
        Assert.Equal(10, properties.GetProperty("ownerLimit").GetProperty("default").GetInt32());
        Assert.Equal(5, properties.GetProperty("context").GetProperty("default").GetInt32());
        Assert.False(properties.GetProperty("details").GetProperty("default").GetBoolean());
    }

    [Fact]
    public async Task InvestigateSeam_ReturnsResolvedNoSupportableSeamWithBoundedCoverageWarnings()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns settlement clearing?",
                ["selector"] = atlas.TargetSymbolId,
                ["relationshipLimit"] = 3,
                ["ownerLimit"] = 5,
                ["context"] = 0,
                ["details"] = true
            });

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var callers = data
            .GetProperty("evidenceSections")
            .EnumerateArray()
            .Single(section => section.GetProperty("family").GetString() == "Callers");

        Assert.Equal("resolved", root.GetProperty("status").GetString());
        Assert.Equal("build-oc32", root.GetProperty("build").GetProperty("resolvedBuildId").GetString());
        Assert.Equal("index-oc32", root.GetProperty("build").GetProperty("indexId").GetString());
        var pinnedProvenance = data.GetProperty("pinnedProvenance");
        Assert.Equal("build-oc32", pinnedProvenance.GetProperty("requestedBuildId").GetString());
        Assert.Equal("build-oc32", pinnedProvenance.GetProperty("resolvedBuildId").GetString());
        Assert.Equal("ScheduleI", pinnedProvenance.GetProperty("codebase").GetString());
        Assert.Equal("Installed", pinnedProvenance.GetProperty("channel").GetString());
        Assert.True(pinnedProvenance.GetProperty("integrityVerified").GetBoolean());
        Assert.Equal("InsufficientCoverage", data.GetProperty("conclusion").GetString());
        Assert.Equal("Resolved", data.GetProperty("resolution").GetProperty("status").GetString());
        Assert.Equal("Game.Clearing.ClearGeneric", data.GetProperty("candidate").GetProperty("qualifiedName").GetString());
        Assert.Contains(
            data.GetProperty("coverageWarnings").EnumerateArray().Select(value => value.GetString()),
            value => string.Equals(value, "Escalation: incomplete caller coverage", StringComparison.Ordinal));
        Assert.Equal("Incomplete", callers.GetProperty("coverage").GetString());
        Assert.Equal(4, callers.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, callers.GetProperty("returnedCount").GetInt32());
        var allowedClassifications = new List<string> { "FACT", "DERIVED", "UNKNOWN" };
        Assert.All(
            root.GetProperty("provenance").EnumerateArray(),
            entry => Assert.Contains(entry.GetProperty("classification").GetString(), allowedClassifications));
        Assert.All(
            data.GetProperty("claims").EnumerateArray(),
            claim => Assert.Contains(claim.GetProperty("classification").GetString(), allowedClassifications));
    }

    [Fact]
    public async Task InvestigateSeam_UsesExplicitOlderBuildAuthorityForResolutionAndProvenance()
    {
        await using var atlas = await SeamMcpTestAtlas.CreatePinnedBuildFixtureAsync();

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which authority owns the pinned seam?",
                ["selector"] = "Game.Seams.PinnedTarget.Run",
                ["buildId"] = atlas.BuildId,
                ["relationshipLimit"] = 10,
                ["ownerLimit"] = 10,
                ["context"] = 0,
                ["details"] = true
            });

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        var data = root.GetProperty("data");

        Assert.Equal("resolved", root.GetProperty("status").GetString());
        Assert.Equal("build-seam-old", root.GetProperty("build").GetProperty("resolvedBuildId").GetString());
        Assert.Equal("index-seam-old", root.GetProperty("build").GetProperty("indexId").GetString());
        Assert.Equal("index-seam-old", data.GetProperty("resolution").GetProperty("symbol").GetProperty("indexId").GetString());
        Assert.Equal("Game.OldAuthority.Owner", data.GetProperty("candidate").GetProperty("qualifiedName").GetString());
        Assert.DoesNotContain(
            data.GetProperty("ownerCandidates").EnumerateArray(),
            candidate => candidate.GetProperty("symbol").GetProperty("qualifiedName").GetString() == "Game.NewAuthority.Owner");
        Assert.Equal("index-seam-old", data.GetProperty("pinnedProvenance").GetProperty("indexId").GetString());
        Assert.All(
            root.GetProperty("provenance").EnumerateArray(),
            entry =>
            {
                Assert.Equal("build-seam-old", entry.GetProperty("buildId").GetString());
                Assert.Equal("index-seam-old", entry.GetProperty("indexId").GetString());
            });
    }

    [Fact]
    public void EnvelopeMapper_MapsResolvedSupportableSeamPacketsWithoutChangingConclusion()
    {
        var authority = new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            "build-supportable-requested",
            "build-supportable",
            "extraction-supportable",
            "index-supportable",
            new IndexRunRecord("index-supportable", "snapshot-supportable", IndexRunStatus.Completed, "2026-08-29T00:00:00Z"),
            null);
        var candidate = Symbol(
            indexId: "index-supportable",
            symbolId: "request-owner",
            kind: SymbolKind.Method.ToString(),
            qualifiedName: "Alpha.RequestBoundary.HandleSupportable",
            signature: "System.Void Alpha.RequestBoundary::HandleSupportable()");
        var result = new SeamInvestigationResult(
            "Which seam owns the supportable request path?",
            SeamConclusion.SupportableSeam,
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidate, []),
            candidate,
            "request",
            BodyRecoveryStatus.Recovered,
            EvidenceCoverage.Complete,
            EvidenceCoverage.NotApplicable,
            [
                new SeamEvidenceClaim(
                    "candidate symbol",
                    "FACT",
                    "Resolved target symbol 'Alpha.RequestBoundary.HandleSupportable' for 'Which seam owns the supportable request path?'.",
                    ["resolution:request-owner"])
            ],
            [
                new SeamEvidenceSection(
                    "Callers",
                    EvidenceCoverage.Complete,
                    1,
                    1,
                    ["caller-001"],
                    null)
            ],
            [
                new SeamOwnerCandidate(
                    candidate,
                    "request",
                    new SeamEvidencePath(["caller-001"], 1, 1),
                    ["caller-001"])
            ],
            [],
            [],
            [],
            new SeamPinnedProvenance(
                "build-supportable-requested",
                "build-supportable",
                "extraction-supportable",
                "index-supportable",
                "ScheduleI",
                "Installed",
                true),
            new SeamAuthorityEntityAttribution(
                "ScheduleI:Installed",
                "Alpha.RequestBoundary.HandleSupportable",
                ["authority-001"]),
            new SeamAlternateGenericCallerEvidence(
                [
                    new SeamOwnerCandidate(
                        candidate,
                        "request",
                        new SeamEvidencePath(["caller-001"], 1, 1),
                        ["caller-001"])
                ],
                true,
                EvidenceCoverage.Complete,
                ["caller-001"]),
            new SeamLifecyclePositionAndBeforeAfterState(
                "request-boundary",
                "validated request",
                "supportable handler invoked",
                EvidenceCoverage.Complete,
                ["lifecycle-001"]),
            new SeamApiBeforePatchResult(
                "PublicMethodWrapper",
                "FACT: callable surface is resolved before patching.",
                EvidenceCoverage.Complete,
                ["api-001"]));

        var envelope = EnvelopeMapper.FromSeamInvestigation(authority, result);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.NotNull(envelope.Build);
        Assert.Equal("build-supportable-requested", envelope.Build!.RequestedBuildId);
        Assert.Equal("build-supportable", envelope.Build.ResolvedBuildId);
        Assert.Equal("index-supportable", envelope.Build.IndexId);
        Assert.NotNull(envelope.Data);
        Assert.Equal(SeamConclusion.SupportableSeam, envelope.Data!.Conclusion);
        Assert.Equal("request", envelope.Data.CandidateRole);
        Assert.Empty(envelope.Candidates);
        Assert.Null(envelope.Error);
        Assert.Collection(
            envelope.Provenance,
            entry =>
            {
                Assert.Equal(ProvenanceClassification.Fact, entry.Classification);
                Assert.Equal("seam-investigation", entry.Source);
                Assert.Equal("build-supportable", entry.BuildId);
                Assert.Equal("extraction-supportable", entry.ExtractionId);
                Assert.Equal("index-supportable", entry.IndexId);
            },
            entry =>
            {
                Assert.Equal(ProvenanceClassification.Derived, entry.Classification);
                Assert.Equal("seam-evaluation", entry.Source);
                Assert.Equal("build-supportable", entry.BuildId);
                Assert.Equal("extraction-supportable", entry.ExtractionId);
                Assert.Equal("index-supportable", entry.IndexId);
            });
    }

    [Fact]
    public void EnvelopeMapper_MapsResolvedNoSupportableSeamPacketsAsResolved()
    {
        var authority = new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            "build-no-supportable-requested",
            "build-no-supportable",
            "extraction-no-supportable",
            "index-no-supportable",
            new IndexRunRecord("index-no-supportable", "snapshot-no-supportable", IndexRunStatus.Completed, "2026-08-29T00:00:00Z"),
            null);
        var candidate = Symbol(
            indexId: "index-no-supportable",
            symbolId: "candidate-no-supportable",
            kind: SymbolKind.Method.ToString(),
            qualifiedName: "Alpha.GenericOwner.Run",
            signature: "System.Void Alpha.GenericOwner::Run()");
        var result = new SeamInvestigationResult(
            "Which seam owns the unresolved request path?",
            SeamConclusion.NoSupportableSeam,
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidate, []),
            candidate,
            "unknown",
            null,
            EvidenceCoverage.NotApplicable,
            EvidenceCoverage.NotApplicable,
            [],
            [],
            [],
            ["Escalation: unresolved owning authority"],
            ["authority/entity attribution"],
            [],
            new SeamPinnedProvenance(
                "build-no-supportable-requested",
                "build-no-supportable",
                "extraction-no-supportable",
                "index-no-supportable",
                "ScheduleI",
                "Installed",
                true),
            new SeamAuthorityEntityAttribution(
                "UNKNOWN",
                "UNKNOWN",
                []),
            new SeamAlternateGenericCallerEvidence(
                [
                    new SeamOwnerCandidate(
                        candidate,
                        "unknown",
                        new SeamEvidencePath(["caller-unknown"], 1, 1),
                        ["caller-unknown"])
                ],
                false,
                EvidenceCoverage.Incomplete,
                ["caller-unknown"]),
            new SeamLifecyclePositionAndBeforeAfterState(
                "UNKNOWN",
                "UNKNOWN",
                "UNKNOWN",
                EvidenceCoverage.Unavailable,
                []),
            new SeamApiBeforePatchResult(
                "UNKNOWN",
                "S1API/S1MAPI evidence is unavailable before patching.",
                EvidenceCoverage.Unavailable,
                []));

        var envelope = EnvelopeMapper.FromSeamInvestigation(authority, result);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(SeamConclusion.NoSupportableSeam, envelope.Data!.Conclusion);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.Source == "seam-investigation" &&
            entry.BuildId == "build-no-supportable" &&
            entry.IndexId == "index-no-supportable");
    }

    [Fact]
    public void EnvelopeMapper_DoesNotExposeResolvedSeamWithMissingGateRecords()
    {
        var authority = new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            "build-incomplete-requested",
            "build-incomplete",
            "extraction-incomplete",
            "index-incomplete",
            new IndexRunRecord("index-incomplete", "snapshot-incomplete", IndexRunStatus.Completed, "2026-08-29T00:00:00Z"),
            null);
        var candidate = Symbol(
            indexId: "index-incomplete",
            symbolId: "candidate-incomplete",
            kind: SymbolKind.Method.ToString(),
            qualifiedName: "Alpha.GenericOwner.Run",
            signature: "System.Void Alpha.GenericOwner::Run()");
        var result = new SeamInvestigationResult(
            "Which seam owns the incomplete packet?",
            SeamConclusion.NoSupportableSeam,
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidate, []),
            candidate,
            "unknown",
            null,
            EvidenceCoverage.NotApplicable,
            EvidenceCoverage.NotApplicable,
            [],
            [],
            [],
            [],
            [],
            []);

        var envelope = EnvelopeMapper.FromSeamInvestigation(authority, result);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("IncompleteSeamResult", envelope.Error!.Code);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task InvestigateSeam_ReferenceScopeCarriesPinnedBuildAndBothIndexAuthorities()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol-seam");
        var tools = new SeamTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var envelope = await tools.InvestigateSeamAsync(
            "Which reference seam owns the mod path?",
            "qol/Qol.Mod::Run():System.Void",
            atlas.BuildIdA,
            "reference",
            reference.Collection,
            10,
            10,
            0,
            true,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdA, envelope.Data!.PinnedProvenance!.RequestedBuildId);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexId, envelope.Build.IndexId);
        Assert.Equal(atlas.BuildIdA, envelope.Data.PinnedProvenance.ResolvedBuildId);
        Assert.Null(envelope.Data.PinnedProvenance.ExtractionId);
        Assert.Equal(reference.IndexId, envelope.Data.PinnedProvenance.IndexId);
        Assert.Equal("ReferenceMod", envelope.Data.PinnedProvenance.Codebase);
        Assert.False(envelope.Data.PinnedProvenance.IntegrityVerified);
        Assert.Equal(reference.IndexId, envelope.Data.Resolution.Symbol!.IndexId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Source == "reference-collection-base" &&
            entry.BuildId == atlas.BuildIdA &&
            entry.IndexId == atlas.IndexId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Source == "reference-collection" &&
            entry.BuildId == atlas.BuildIdA &&
            entry.IndexId == reference.IndexId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Source == "seam-investigation" &&
            entry.BuildId == atlas.BuildIdA &&
            entry.ExtractionId is null &&
            entry.IndexId == reference.IndexId);
    }

    [Fact]
    public async Task InvestigateSeam_ReferenceScopeNotFoundRetainsPinnedReferenceProvenanceForGameOnlySelector()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol-seam-not-found");
        var tools = new SeamTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var envelope = await tools.InvestigateSeamAsync(
            "Which reference seam owns the game-only path?",
            atlas.MethodSelector,
            atlas.BuildIdA,
            "reference",
            reference.Collection,
            10,
            10,
            0,
            false,
            CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.RequestedBuildId);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexId, envelope.Build.IndexId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Source == "reference-collection-base" &&
            entry.BuildId == atlas.BuildIdA &&
            entry.IndexId == atlas.IndexId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Source == "reference-collection" &&
            entry.BuildId == atlas.BuildIdA &&
            entry.IndexId == reference.IndexId);
    }

    [Fact]
    public async Task InvestigateSeam_DetailsProjectionPreservesGateRecordsAndUnknownApiBeforePatch()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();
        var baseArguments = new Dictionary<string, object?>
        {
            ["behavioralQuestion"] = "Which seam owns settlement clearing?",
            ["selector"] = atlas.TargetSymbolId,
            ["relationshipLimit"] = 3,
            ["ownerLimit"] = 5,
            ["context"] = 0
        };

        var summaryArguments = new Dictionary<string, object?>(baseArguments)
        {
            ["details"] = false
        };
        var detailsArguments = new Dictionary<string, object?>(baseArguments)
        {
            ["details"] = true
        };
        using var summaryDocument = JsonDocument.Parse(
            await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", summaryArguments));
        using var detailsDocument = JsonDocument.Parse(
            await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", detailsArguments));
        var summaryRoot = summaryDocument.RootElement;
        var detailsRoot = detailsDocument.RootElement;
        var summaryData = summaryRoot.GetProperty("data");
        var detailsData = detailsRoot.GetProperty("data");

        Assert.Equal("resolved", summaryRoot.GetProperty("status").GetString());
        Assert.Equal(summaryRoot.GetProperty("build").GetRawText(), detailsRoot.GetProperty("build").GetRawText());
        Assert.Equal(summaryRoot.GetProperty("provenance").GetRawText(), detailsRoot.GetProperty("provenance").GetRawText());
        Assert.Equal("InsufficientCoverage", summaryData.GetProperty("conclusion").GetString());
        Assert.Equal("UNKNOWN", summaryData.GetProperty("apiBeforePatchResult").GetProperty("apiSurface").GetString());
        Assert.Equal("Unavailable", summaryData.GetProperty("apiBeforePatchResult").GetProperty("coverage").GetString());
        Assert.Contains("S1API/S1MAPI evidence is unavailable", summaryData.GetProperty("apiBeforePatchResult").GetProperty("result").GetString());

        foreach (var field in new[]
        {
            "behavioralQuestion",
            "conclusion",
            "resolution",
            "candidate",
            "candidateRole",
            "bodyRecoveryStatus",
            "bodyCoverage",
            "callableCoverage",
            "ownerCandidates",
            "coverageWarnings",
            "unknownDimensions",
            "nextActions",
            "pinnedProvenance",
            "authorityEntityAttribution",
            "alternateGenericCallersAndExclusivity",
            "lifecyclePositionAndBeforeAfterState",
            "apiBeforePatchResult"
        })
        {
            Assert.NotEqual(JsonValueKind.Null, summaryData.GetProperty(field).ValueKind);
            Assert.Equal(summaryData.GetProperty(field).GetRawText(), detailsData.GetProperty(field).GetRawText());
        }

        Assert.Equal("build-oc32", summaryData.GetProperty("pinnedProvenance").GetProperty("resolvedBuildId").GetString());
        Assert.Equal("index-oc32", summaryData.GetProperty("pinnedProvenance").GetProperty("indexId").GetString());
        Assert.Equal("UNKNOWN", summaryData.GetProperty("authorityEntityAttribution").GetProperty("authority").GetString());
        Assert.Equal("UNKNOWN", summaryData.GetProperty("lifecyclePositionAndBeforeAfterState").GetProperty("beforeState").GetString());
        Assert.Empty(summaryData.GetProperty("claims").EnumerateArray());
        Assert.Empty(summaryData.GetProperty("evidenceSections").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("claims").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("evidenceSections").EnumerateArray());
    }

    [Fact]
    public async Task InvestigateSeam_ReferenceScopeDetailsProjectionPreservesResolvedReferenceAndBaseAuthorityMetadata()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol-seam-summary");
        var baseArguments = new Dictionary<string, object?>
        {
            ["behavioralQuestion"] = "Which reference seam owns the mod path?",
            ["selector"] = "qol/Qol.Mod::Run():System.Void",
            ["buildId"] = atlas.BuildIdA,
            ["scope"] = "reference",
            ["collection"] = reference.Collection,
            ["relationshipLimit"] = 10,
            ["ownerLimit"] = 10,
            ["context"] = 0
        };

        var summaryArguments = new Dictionary<string, object?>(baseArguments)
        {
            ["details"] = false
        };
        var detailsArguments = new Dictionary<string, object?>(baseArguments)
        {
            ["details"] = true
        };
        using var summaryDocument = JsonDocument.Parse(
            await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", summaryArguments));
        using var detailsDocument = JsonDocument.Parse(
            await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", detailsArguments));
        var summaryRoot = summaryDocument.RootElement;
        var detailsRoot = detailsDocument.RootElement;
        var summaryData = summaryRoot.GetProperty("data");
        var detailsData = detailsRoot.GetProperty("data");

        Assert.Equal("resolved", summaryRoot.GetProperty("status").GetString());
        Assert.Equal(summaryRoot.GetProperty("build").GetRawText(), detailsRoot.GetProperty("build").GetRawText());
        Assert.Equal(summaryRoot.GetProperty("provenance").GetRawText(), detailsRoot.GetProperty("provenance").GetRawText());
        Assert.Equal(summaryData.GetProperty("pinnedProvenance").GetRawText(), detailsData.GetProperty("pinnedProvenance").GetRawText());
        Assert.Equal(summaryData.GetProperty("authorityEntityAttribution").GetRawText(), detailsData.GetProperty("authorityEntityAttribution").GetRawText());
        Assert.Equal(summaryData.GetProperty("alternateGenericCallersAndExclusivity").GetRawText(), detailsData.GetProperty("alternateGenericCallersAndExclusivity").GetRawText());
        Assert.Equal(summaryData.GetProperty("lifecyclePositionAndBeforeAfterState").GetRawText(), detailsData.GetProperty("lifecyclePositionAndBeforeAfterState").GetRawText());
        Assert.Equal(summaryData.GetProperty("apiBeforePatchResult").GetRawText(), detailsData.GetProperty("apiBeforePatchResult").GetRawText());

        Assert.Equal(atlas.BuildIdA, summaryRoot.GetProperty("build").GetProperty("resolvedBuildId").GetString());
        Assert.Equal(atlas.IndexId, summaryRoot.GetProperty("build").GetProperty("indexId").GetString());
        Assert.Equal(atlas.BuildIdA, summaryData.GetProperty("pinnedProvenance").GetProperty("resolvedBuildId").GetString());
        Assert.Equal(reference.IndexId, summaryData.GetProperty("pinnedProvenance").GetProperty("indexId").GetString());
        Assert.Equal("ReferenceMod", summaryData.GetProperty("pinnedProvenance").GetProperty("codebase").GetString());
        Assert.Null(summaryData.GetProperty("pinnedProvenance").GetProperty("extractionId").GetString());
        Assert.Contains(
            McpProvenanceIdentifiers(summaryRoot),
            value => value == $"reference-collection-base|{atlas.BuildIdA}|{atlas.ExtractionIdA}|{atlas.IndexId}");
        Assert.Contains(
            McpProvenanceIdentifiers(summaryRoot),
            value => value == $"reference-collection|{atlas.BuildIdA}||{reference.IndexId}");
        Assert.Contains(
            McpProvenanceIdentifiers(summaryRoot),
            value => value == $"seam-investigation|{atlas.BuildIdA}||{reference.IndexId}");
        Assert.Contains(
            McpProvenanceIdentifiers(summaryRoot),
            value => value == $"seam-evaluation|{atlas.BuildIdA}||{reference.IndexId}");
        Assert.Empty(summaryData.GetProperty("claims").EnumerateArray());
        Assert.Empty(summaryData.GetProperty("evidenceSections").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("claims").EnumerateArray());
        Assert.NotEmpty(detailsData.GetProperty("evidenceSections").EnumerateArray());
    }

    [Fact]
    public async Task InvestigateSeam_ReturnsAmbiguousStatusAndCandidates()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateAmbiguousAsync();

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns the ambiguous path?",
                ["selector"] = "Game.Seams.Ambiguous.Run"
            });

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;

        Assert.Equal("ambiguous", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("candidates").GetArrayLength());
        Assert.True(!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null);
    }

    [Theory]
    [InlineData("relationshipLimit", 0, "InvalidRelationshipLimit")]
    [InlineData("relationshipLimit", 51, "InvalidRelationshipLimit")]
    [InlineData("ownerLimit", 0, "InvalidOwnerLimit")]
    [InlineData("ownerLimit", 51, "InvalidOwnerLimit")]
    [InlineData("context", -1, "InvalidContext")]
    public async Task InvestigateSeam_RejectsInvalidLimits(
        string argumentName,
        int argumentValue,
        string expectedCode)
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();
        var arguments = new Dictionary<string, object?>
        {
            ["behavioralQuestion"] = "Which seam owns settlement clearing?",
            ["selector"] = atlas.TargetSymbolId
        };
        arguments[argumentName] = argumentValue;

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            arguments);

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;

        Assert.Equal("invalid", root.GetProperty("status").GetString());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvestigateSeam_UsesUnavailableStatusWhenNoCurrentBuildExists()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateBareAsync();

        var serialized = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns settlement clearing?",
                ["selector"] = "Game.Seams.Target.Run"
            });

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;

        Assert.Equal("unavailable", root.GetProperty("status").GetString());
        Assert.Equal("NoCurrentBuild", root.GetProperty("error").GetProperty("code").GetString());
        Assert.True(!root.TryGetProperty("build", out var build) || build.ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task InvestigateSeam_SerializationIsDeterministicAcrossRepeatedRuns()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();

        var arguments = new Dictionary<string, object?>
        {
            ["behavioralQuestion"] = "Which seam owns settlement clearing?",
            ["selector"] = atlas.TargetSymbolId,
            ["relationshipLimit"] = 3,
            ["ownerLimit"] = 5,
            ["context"] = 0
        };

        var first = await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", arguments);
        var second = await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", arguments);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvestigateSeam_McpAndCliJsonStayInCompletePacketParityForBothDetailModes(bool details)
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();

        var arguments = new Dictionary<string, object?>
        {
            ["behavioralQuestion"] = "Which seam owns settlement clearing?",
            ["selector"] = atlas.TargetSymbolId,
            ["relationshipLimit"] = 3,
            ["ownerLimit"] = 5,
            ["context"] = 0,
            ["details"] = details
        };

        var cli = RunCli(atlas.DataRoot, atlas.TargetSymbolId, details);
        var mcp = await McpTestHost.CallToolThroughStdioAsync(atlas.DataRoot, "investigate_seam", arguments);

        using var cliDocument = JsonDocument.Parse(cli.StandardOutput);
        using var mcpDocument = JsonDocument.Parse(mcp);
        var cliData = cliDocument.RootElement.GetProperty("data");
        var mcpRoot = mcpDocument.RootElement;
        var mcpData = mcpRoot.GetProperty("data");
        var build = mcpRoot.GetProperty("build");
        var resolvedBuildId = build.GetProperty("resolvedBuildId").GetString();
        var extractionId = build.GetProperty("extractionId").GetString();
        var indexId = build.GetProperty("indexId").GetString();

        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(string.Empty, cli.StandardError);
        Assert.True(cliDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, cliDocument.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("investigate_seam", cliDocument.RootElement.GetProperty("command").GetString());
        Assert.Equal("resolved", mcpRoot.GetProperty("status").GetString());
        AssertCompleteSharedPacketEquivalent(cliData, mcpData);
        AssertRequiredGateRecordsPresent(cliData);
        AssertRequiredGateRecordsPresent(mcpData);
        Assert.True(cliData.TryGetProperty("referenceCollectionBaseProvenance", out var cliBaseProvenance));
        Assert.Equal(JsonValueKind.Null, cliBaseProvenance.ValueKind);
        Assert.False(mcpData.TryGetProperty("referenceCollectionBaseProvenance", out _));
        Assert.Equal(details, cliData.GetProperty("claims").GetArrayLength() > 0);
        Assert.Equal(details, cliData.GetProperty("evidenceSections").GetArrayLength() > 0);
        Assert.Equal(
            ExpectedMcpProvenanceIdentifiers(atlas, cliData),
            McpProvenanceIdentifiers(mcpRoot));
        Assert.Equal(atlas.BuildId, resolvedBuildId);
        Assert.Equal(atlas.PreferredExtractionId, extractionId);
        Assert.Equal(atlas.IndexId, indexId);
    }

    private static void AssertSchema(
        string serializedSchema,
        IReadOnlyList<string> expectedProperties,
        IReadOnlyList<string> expectedRequired)
    {
        using var schema = JsonDocument.Parse(serializedSchema);
        var properties = schema.RootElement.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement.EnumerateObject().Select(property => property.Name)
            : [];
        var required = schema.RootElement.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(value => value.GetString()!)
            : [];
        Assert.Equal(
            expectedProperties.OrderBy(value => value, StringComparer.Ordinal),
            properties.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            expectedRequired.OrderBy(value => value, StringComparer.Ordinal),
            required.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunCli(
        string dataRoot,
        string selector,
        bool details)
    {
        var application = new CliApplication(dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = new List<string>
        {
            "investigate_seam",
            selector,
            "--question",
            "Which seam owns settlement clearing?",
            "--relationship-limit",
            "3",
            "--owner-limit",
            "5",
            "--context",
            "0"
        };
        if (details)
            arguments.Add("--details");
        arguments.Add("--json");
        var exitCode = application.Invoke(
            arguments.ToArray(),
            output,
            error,
            CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static void AssertCompleteSharedPacketEquivalent(JsonElement cliData, JsonElement mcpData)
    {
        var cliSharedPacket = new JsonObject();
        foreach (var property in cliData.EnumerateObject())
        {
            if (property.Name != "referenceCollectionBaseProvenance")
                cliSharedPacket[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        var mcpPacket = JsonNode.Parse(mcpData.GetRawText());
        Assert.True(
            JsonNode.DeepEquals(cliSharedPacket, mcpPacket),
            $"CLI and MCP shared seam packets differ. CLI: {cliSharedPacket} MCP: {mcpPacket}");
    }

    private static void AssertRequiredGateRecordsPresent(JsonElement data)
    {
        foreach (var propertyName in new[]
        {
            "pinnedProvenance",
            "authorityEntityAttribution",
            "alternateGenericCallersAndExclusivity",
            "lifecyclePositionAndBeforeAfterState",
            "apiBeforePatchResult"
        })
        {
            Assert.True(data.TryGetProperty(propertyName, out var gateRecord), $"Missing {propertyName}.");
            Assert.Equal(JsonValueKind.Object, gateRecord.ValueKind);
        }
    }

    private static string[] ExpectedMcpProvenanceIdentifiers(SeamMcpTestAtlas atlas, JsonElement cliData)
    {
        var cliIndexId = cliData.GetProperty("resolution").GetProperty("symbol").GetProperty("indexId").GetString()!;
        return
        [
            $"seam-evaluation|{atlas.BuildId}|{atlas.PreferredExtractionId}|{cliIndexId}",
            $"seam-investigation|{atlas.BuildId}|{atlas.PreferredExtractionId}|{cliIndexId}"
        ];
    }

    private static string[] McpProvenanceIdentifiers(JsonElement root) =>
        root.GetProperty("provenance")
            .EnumerateArray()
            .Select(item => string.Join(
                "|",
                item.GetProperty("source").GetString(),
                item.GetProperty("buildId").GetString(),
                item.GetProperty("extractionId").GetString(),
                item.GetProperty("indexId").GetString()))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static SymbolQueryResult Symbol(
        string indexId,
        string symbolId,
        string kind,
        string qualifiedName,
        string signature) =>
        new(
            indexId,
            "ScheduleI",
            "Installed",
            symbolId,
            kind,
            qualifiedName,
            signature,
            false);
}

internal sealed class SeamMcpTestAtlas : IAsyncDisposable
{
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-29T00:00:00Z");

    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private SeamMcpTestAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }
    public string TargetSymbolId { get; private set; } = string.Empty;
    public string BuildId { get; private set; } = string.Empty;
    public string PreferredExtractionId { get; private set; } = string.Empty;
    public string IndexId { get; private set; } = string.Empty;
    public string NativeSymbolId { get; private set; } = string.Empty;

    public static async Task<SeamMcpTestAtlas> CreateOc32Async()
    {
        var atlas = await CreateEmptyAsync("oc32");
        var target = await atlas.SeedOc32FixtureAsync();
        atlas.TargetSymbolId = target.SymbolId;
        return atlas;
    }

    public static async Task<SeamMcpTestAtlas> CreatePinnedBuildFixtureAsync()
    {
        var atlas = await CreateEmptyAsync("pinned-build");

        const string oldBuildId = "build-seam-old";
        await atlas.SeedValidatedExtractionOnlyAsync(oldBuildId);
        var oldExtractionId = atlas.PreferredExtractionId;
        var oldTarget = Method("target-seam-old", "snapshot-seam-old", "Game.Seams.PinnedTarget.Run", BodyRecoveryStatus.Recovered);
        var oldOwner = Method("owner-seam-old", "snapshot-seam-old", "Game.OldAuthority.Owner", BodyRecoveryStatus.Recovered);
        await atlas.CompleteGameRunAsync(
            oldBuildId,
            "index-seam-old",
            "snapshot-seam-old",
            oldTarget,
            [oldOwner],
            [Edge("caller-seam-old", "snapshot-seam-old", oldOwner.SymbolId, oldTarget.SymbolId, null, "Calls")],
            includeCallableSurface: true);

        const string newBuildId = "build-seam-new";
        await atlas.SeedValidatedExtractionOnlyAsync(newBuildId);
        var newTarget = Method("target-seam-new", "snapshot-seam-new", "Game.Seams.PinnedTarget.Run", BodyRecoveryStatus.Recovered);
        var newOwner = Method("owner-seam-new", "snapshot-seam-new", "Game.NewAuthority.Owner", BodyRecoveryStatus.Recovered);
        await atlas.CompleteGameRunAsync(
            newBuildId,
            "index-seam-new",
            "snapshot-seam-new",
            newTarget,
            [newOwner],
            [Edge("caller-seam-new", "snapshot-seam-new", newOwner.SymbolId, newTarget.SymbolId, null, "Calls")],
            includeCallableSurface: true,
            completionOffsetMinutes: 5);

        atlas.BuildId = oldBuildId;
        atlas.PreferredExtractionId = oldExtractionId;
        atlas.IndexId = "index-seam-old";
        atlas.TargetSymbolId = oldTarget.SymbolId;
        return atlas;
    }

    public async Task SeedNativeRecoveredEvidenceAsync()
    {
        var snapshot = Assert.IsType<EnvironmentSnapshot>(
            await _repository.GetCurrentSnapshotAsync(CancellationToken.None));
        NativeSymbolId = "native-target";
        var request = new NativeRecoveryRequest(
            BuildId,
            IndexId,
            snapshot.Build.GameAssemblySha256,
            [NativeSymbolId],
            25);
        await _repository.SaveNativeRecoveryAsync(
            new NativeRecoveryRecord(
                new string('a', 64),
                request,
                "native-tool",
                "1.0.0",
                new string('b', 64),
                NativeRecoveryStatus.Recovered,
                ["managed pointer 0x100"],
                [new NativeEvidenceEdge(
                    new string('c', 64),
                    "0x200",
                    "0x220",
                    "Native.Target",
                    "DirectCall",
                    "direct native target",
                    true)],
                [],
                true,
                new string('d', 64),
                DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
                null),
            CancellationToken.None);
    }

    public static Task<SeamMcpTestAtlas> CreateBareAsync() =>
        CreateEmptyAsync("bare");

    public static async Task<SeamMcpTestAtlas> CreateAmbiguousAsync()
    {
        var atlas = await CreateEmptyAsync("ambiguous");
        await atlas.SeedAmbiguousFixtureAsync();
        return atlas;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static async Task<SeamMcpTestAtlas> CreateEmptyAsync(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-seam-mcp-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        var atlas = new SeamMcpTestAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(CancellationToken.None);
        await atlas.SeedToolInstanceAsync();
        return atlas;
    }

    private async Task<IndexSymbolRecord> SeedOc32FixtureAsync()
    {
        const string buildId = "build-oc32";
        BuildId = buildId;
        await SeedValidatedExtractionOnlyAsync(buildId);

        var target = Method("target-oc32", "snapshot-oc32", "Game.Seams.Target.Run", BodyRecoveryStatus.Recovered);
        var type = Type("type-free-server", "snapshot-oc32", "Game.Free_Server");
        var requestBoundary = Method("request-boundary", "snapshot-oc32", "Game.RequestBoundary.HandleSettlementRequest", BodyRecoveryStatus.Recovered);
        var genericClearing = Method("generic-clearing", "snapshot-oc32", "Game.Clearing.ClearGeneric", BodyRecoveryStatus.Recovered);
        var freeRelease = Method("free-release", "snapshot-oc32", "Game.Free_Server.Release", BodyRecoveryStatus.Recovered);
        var uiSettlement = Method("ui-settlement", "snapshot-oc32", "UI.SettlementPanel.ApplySettlementWithoutPlayer", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            buildId,
            "index-oc32",
            "snapshot-oc32",
            target,
            [type, requestBoundary, genericClearing, freeRelease, uiSettlement],
            [
                Edge("caller-001-request", "snapshot-oc32", requestBoundary.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-002-generic-clear", "snapshot-oc32", genericClearing.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-003-free-release", "snapshot-oc32", freeRelease.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-004-ui-settlement", "snapshot-oc32", uiSettlement.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);
        IndexId = "index-oc32";

        return target;
    }

    private async Task SeedAmbiguousFixtureAsync()
    {
        const string buildId = "build-ambiguous";
        await SeedValidatedExtractionOnlyAsync(buildId);

        var targetA = Method("ambiguous-a", "snapshot-ambiguous", "Game.Seams.Ambiguous.Run", BodyRecoveryStatus.Recovered);
        var targetB = new IndexSymbolRecord(
            "ambiguous-b",
            "snapshot-ambiguous",
            "ScheduleI:Installed:Method:Game.Seams.Ambiguous::Run(System.Int32)",
            "Method",
            "Game.Seams.Ambiguous.Run",
            "System.Void Game.Seams.Ambiguous::Run(System.Int32)",
            false,
            BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            buildId,
            "index-ambiguous",
            "snapshot-ambiguous",
            targetA,
            [targetB],
            [],
            includeCallableSurface: false);
    }

    private async Task SeedValidatedExtractionOnlyAsync(string buildId)
    {
        await SeedSnapshotAsync(buildId);
        var extractionId = await SeedValidatedExtractionAsync(buildId, SeedForBuild(buildId));
        PreferredExtractionId = extractionId;
        await _repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                extractionId,
                BaseTime.AddMinutes(2),
                ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
    }

    private async Task<string> SeedValidatedExtractionAsync(string buildId, string seed)
    {
        var recipeId = seed.PadLeft(64, seed[0]);
        var manifest = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/Assembly-CSharp.dll",
                ArtifactKind.ManagedAssembly,
                6,
                Convert.ToHexString(SHA256.HashData([10, 20, 30, 40, 50, 60])).ToLowerInvariant(),
                "Assembly-CSharp",
                "Assembly-CSharp.dll",
                1,
                1,
                0,
                0,
                0)
        ]);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var attempt = await CreateValidatingAttemptAsync(buildId, recipeId, extractionId[..32]);
        var statistics = new ExtractionStatistics(
            1,
            1,
            1,
            1,
            1,
            0,
            0,
            0,
            6,
            6,
            [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 6, 1, 1, 0, 0, 0)]);
        var extractionRoot = Path.Combine(DataRoot, "builds", buildId, "extractions", extractionId);
        var extraction = new ValidatedExtraction(
            extractionId,
            recipeId,
            buildId,
            ToolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            ProfileDigest,
            1,
            1,
            digest,
            extractionRoot,
            BaseTime.AddMinutes(1),
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            statistics);
        var report = new ValidationReport(
            1,
            attempt.AttemptId,
            ValidationSubjectKind.CandidateOutput,
            null,
            buildId,
            recipeId,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            ValidationOutcome.Valid,
            true,
            true,
            true,
            digest,
            statistics,
            null,
            [],
            [],
            true,
            BaseTime.AddMinutes(2));
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        await File.WriteAllBytesAsync(
            Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"),
            [10, 20, 30, 40, 50, 60]);
        await WriteValidatedExtractionDocumentsAsync(extractionRoot, extraction, manifest, report);
        await _repository.CommitValidatedExtractionAsync(
            new ValidatedExtractionPromotion(
                attempt with
                {
                    Status = ExtractionAttemptStatus.Succeeded,
                    CompletedAtUtc = BaseTime.AddMinutes(2),
                    ResultExtractionId = extractionId
                },
                extraction,
                manifest,
                report,
                null),
            CancellationToken.None);
        return extractionId;
    }

    private async Task CompleteGameRunAsync(
        string buildId,
        string indexId,
        string snapshotId,
        IndexSymbolRecord target,
        IReadOnlyList<IndexSymbolRecord> additionalSymbols,
        IReadOnlyList<IndexRelationshipRecord> relationships,
        bool includeCallableSurface,
        int completionOffsetMinutes = 4)
    {
        var extractionId = (await _repository.GetPreferredExtractionAsync(buildId, CancellationToken.None))!.ExtractionId;
        var environmentSnapshot = Assert.IsType<EnvironmentSnapshot>(
            await _repository.GetCurrentSnapshotAsync(CancellationToken.None));
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            BaseTime.AddMinutes(3).ToString("O"),
            EnvironmentSnapshotId.Create(environmentSnapshot));
        await _repository.CreateCodeSnapshotAsync(snapshot, CancellationToken.None);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            CancellationToken.None);

        var sourceText = "namespace Game.Seams;\npublic class Target\n{\n    public void Run()\n    {\n        return;\n    }\n}\n";
        var sourceFile = new IndexSourceFileRecord(
            "file-" + indexId,
            snapshotId,
            "Assembly-CSharp.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var sourceLocation = new IndexSourceLocationRecord(target.SymbolId, sourceFile.SourceFileId, 4, 5, 7, 6);

        var symbols = new List<IndexSymbolRecord> { target };
        symbols.AddRange(additionalSymbols);
        var writeSet = new IndexWriteSet(
            symbols,
            [sourceFile],
            [sourceLocation],
            [],
            relationships,
            includeCallableSurface
                ? [
                    new IndexCallableSurfaceRecord(
                        "surface-" + indexId,
                        indexId,
                        snapshotId,
                        target.SymbolId,
                        target.CanonicalKey,
                        "Assembly-CSharp.dll",
                        "interop-" + indexId,
                        target.Signature,
                        CallableSurfaceKind.PublicMethodWrapper,
                        false,
                        CallableSurfaceStatus.Resolved,
                        InteropInputTrust.LocalOnly,
                        "wrapper forwards through il2cpp_runtime_invoke")
                ]
                : []);
        await _repository.CompleteIndexRunAsync(
            indexId,
            writeSet,
            BaseTime.AddMinutes(completionOffsetMinutes).ToString("O"),
            CancellationToken.None);

        var indexRoot = Path.Combine(DataRoot, "builds", buildId, "indexes", indexId);
        Directory.CreateDirectory(indexRoot);
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, sourceFile.RelativePath),
            sourceText,
            new UTF8Encoding(false),
            CancellationToken.None);
    }

    private async Task<ExtractionAttempt> CreateValidatingAttemptAsync(string buildId, string recipeId, string attemptId)
    {
        var created = new ExtractionAttempt(
            attemptId,
            recipeId,
            buildId,
            ToolInstanceId,
            "default-profile",
            1,
            ProfileDigest,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            1,
            1,
            ExtractionInputSource.Live,
            null,
            ExtractionAttemptStatus.Created,
            BaseTime,
            null,
            null,
            null,
            null,
            $"C:\\attempts\\{attemptId}\\work",
            $"C:\\attempts\\{attemptId}\\stdout.log",
            $"C:\\attempts\\{attemptId}\\stderr.log",
            false,
            false,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            false,
            0,
            0,
            null,
            null);
        await _repository.CreateAttemptAsync(created, CancellationToken.None);
        var preparing = created with { Status = ExtractionAttemptStatus.Preparing, StartedAtUtc = BaseTime };
        await _repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, CancellationToken.None);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 1234 };
        await _repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, CancellationToken.None);
        var completed = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = "C:\\candidate"
        };
        await _repository.TransitionAttemptAsync(completed, ExtractionAttemptStatus.Running, CancellationToken.None);
        var validating = completed with { Status = ExtractionAttemptStatus.Validating };
        await _repository.TransitionAttemptAsync(validating, ExtractionAttemptStatus.ProcessCompleted, CancellationToken.None);
        return validating;
    }

    private Task SeedSnapshotAsync(string buildId) =>
        _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, Sha256("assembly-" + buildId), Sha256("metadata-" + buildId), BaseTime, true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                BaseTime),
            CancellationToken.None);

    private async Task SeedToolInstanceAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DataRoot, "atlas.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO tool_instances (tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ($id, 'cpp2il', 'test', 'win-x64', 'ManagedPinned', 'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe', '2026-08-29T00:00:00.0000000+00:00', '2026-08-29T00:05:00.0000000+00:00', 'Verified');""";
        command.Parameters.AddWithValue("$id", ToolInstanceId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteValidatedExtractionDocumentsAsync(
        string extractionRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report)
    {
        var extractionAssembly = typeof(ValidatedExtractionIntegrityVerifier).Assembly;
        var storeType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionDocumentStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(storeType)
            ?? throw new InvalidOperationException("Could not create validated extraction document store.");
        var writeMethod = storeType.GetMethod("WriteFinalDocumentsAsync")
            ?? throw new InvalidOperationException("Validated extraction document writer was not found.");
        var writeTask = (Task)writeMethod.Invoke(
            store,
            [DataRoot, extractionRoot, extraction, manifest, report, CancellationToken.None])!;
        await writeTask;
    }

    private static IndexSymbolRecord Method(
        string id,
        string snapshotId,
        string qualifiedName,
        BodyRecoveryStatus status)
    {
        var member = CanonicalMember(qualifiedName);
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + member,
            "Method",
            qualifiedName,
            "System.Void " + member,
            false,
            status);
    }

    private static IndexSymbolRecord Type(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Type:" + qualifiedName,
            "Type",
            qualifiedName,
            qualifiedName,
            false);

    private static IndexRelationshipRecord Edge(
        string id,
        string snapshotId,
        string source,
        string? target,
        string? targetText,
        string kind) =>
        new(id, snapshotId, source, target, targetText, kind, "fixture:" + id);

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName + "()"
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..] + "()";
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SeedForBuild(string buildId) =>
        buildId switch
        {
            "build-oc32" => "1",
            "build-ambiguous" => "2",
            "build-seam-old" => "4",
            "build-seam-new" => "5",
            _ => "3"
        };
}
