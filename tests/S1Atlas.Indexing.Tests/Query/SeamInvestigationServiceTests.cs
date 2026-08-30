using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class SeamInvestigationServiceTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-seam-investigation-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly SqliteAtlasRepository _repository;

    public SeamInvestigationServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task InvestigateAsync_returns_negative_seam_for_oc32_and_keeps_generic_evidence_visible()
    {
        var fixture = await SeedOc32FixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam owns settlement clearing?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
            RelationshipLimit: 3,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(SeamConclusion.InsufficientCoverage, result.Conclusion);
        Assert.Null(result.NativeEvidence);
        Assert.Contains(result.UnknownDimensions, value => value.Contains("lifecycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.CoverageWarnings, value => value.Contains("caller", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.OwnerCandidates, candidate => candidate.Symbol.QualifiedName == "Game.Free_Server");
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.QualifiedName == "Game.Clearing.ClearGeneric");
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.QualifiedName == "Game.Free_Server.Release");

        var callers = Assert.Single(result.EvidenceSections, section => section.Family == "Callers");
        Assert.Equal(EvidenceCoverage.Incomplete, callers.Coverage);
        Assert.Equal(4, callers.TotalCount);
        Assert.Equal(3, callers.ReturnedCount);

        var claims = result.Claims.Select(claim => claim.Statement);
        Assert.Contains(claims, statement => statement.Contains("multiple owner candidates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvestigateAsync_attaches_opt_in_native_evidence_without_mixing_it_with_managed_edges()
    {
        var fixture = await SeedOc32FixtureAsync();
        var environment = Assert.IsType<EnvironmentSnapshot>(
            await _repository.GetCurrentSnapshotAsync(TestContext.Current.CancellationToken));
        var nativeRequest = new NativeRecoveryRequest(
            fixture.BuildId,
            fixture.IndexId,
            environment.Build.GameAssemblySha256,
            ["native-target"],
            25);
        var nativeRecord = new NativeRecoveryRecord(
            new string('a', 64),
            nativeRequest,
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
            null);
        await _repository.SaveNativeRecoveryAsync(nativeRecord, TestContext.Current.CancellationToken);

        var result = await CreateService(includeNative: true).InvestigateAsync(
            new SeamInvestigationRequest(
                "Which seam owns settlement clearing?",
                fixture.Target.SymbolId,
                new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
                RelationshipLimit: 3,
                OwnerCandidateLimit: 5,
                SourceContext: 0,
                IncludeDetails: true,
                NativeSymbolIds: ["native-target"],
                NativeTraversalBudget: 25),
            TestContext.Current.CancellationToken);

        var native = Assert.IsType<NativeEvidenceSummary>(result.NativeEvidence);
        Assert.Equal(NativeRecoveryStatus.Recovered, native.Status);
        Assert.True(native.IsComplete);
        Assert.Single(native.DirectEdges);
        Assert.DoesNotContain(result.EvidenceSections, section => section.Family == "Native");
        Assert.Contains(result.EvidenceSections, section => section.Family == "Callers");
    }

    [Fact]
    public async Task InvestigateAsync_surfaces_no_body_native_result_as_targeted_next_action()
    {
        var fixture = await SeedOc32FixtureAsync();
        var environment = Assert.IsType<EnvironmentSnapshot>(
            await _repository.GetCurrentSnapshotAsync(TestContext.Current.CancellationToken));
        var nativeRecord = new NativeRecoveryRecord(
            new string('e', 64),
            new NativeRecoveryRequest(fixture.BuildId, fixture.IndexId, environment.Build.GameAssemblySha256, ["native-target"], 25),
            "native-tool",
            "1.0.0",
            new string('b', 64),
            NativeRecoveryStatus.NoBody,
            ["managed pointer 0x100"],
            [],
            [],
            false,
            new string('f', 64),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            "No recoverable native body.");
        await _repository.SaveNativeRecoveryAsync(nativeRecord, TestContext.Current.CancellationToken);

        var result = await CreateService(includeNative: true).InvestigateAsync(
            new SeamInvestigationRequest(
                "Which seam owns settlement clearing?",
                fixture.Target.SymbolId,
                new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
                RelationshipLimit: 3,
                OwnerCandidateLimit: 5,
                SourceContext: 0,
                NativeSymbolIds: ["native-target"],
                NativeTraversalBudget: 25),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.NoBody, Assert.IsType<NativeEvidenceSummary>(result.NativeEvidence).Status);
        var action = Assert.Single(result.NextActions, item => item.Kind == "targeted-native-recovery");
        Assert.Contains("no recoverable body", action.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvestigateAsync_distinguishes_missing_native_record_from_unsupported_recovery()
    {
        var fixture = await SeedOc32FixtureAsync();
        var result = await CreateService(includeNative: true).InvestigateAsync(
            new SeamInvestigationRequest(
                "Which seam owns settlement clearing?",
                fixture.Target.SymbolId,
                new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
                RelationshipLimit: 3,
                OwnerCandidateLimit: 5,
                SourceContext: 0,
                NativeSymbolIds: ["missing-native-target"],
                NativeTraversalBudget: 25),
            TestContext.Current.CancellationToken);

        var native = Assert.IsType<NativeEvidenceSummary>(result.NativeEvidence);
        Assert.Equal(NativeEvidenceLookupStatus.NoMatch, native.LookupStatus);
        Assert.Equal(NativeRecoveryStatus.Unsupported, native.Status);
        Assert.Equal("native-recovery-record-not-found", native.ToolProvenance);
        Assert.Contains(
            result.NextActions,
            action => action.Kind == "targeted-native-recovery" &&
                action.Reason.Contains("No matching native recovery record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvestigateAsync_preserves_failed_native_record_message_and_escalation()
    {
        var fixture = await SeedOc32FixtureAsync();
        var environment = Assert.IsType<EnvironmentSnapshot>(
            await _repository.GetCurrentSnapshotAsync(TestContext.Current.CancellationToken));
        var nativeRecord = new NativeRecoveryRecord(
            new string('1', 64),
            new NativeRecoveryRequest(fixture.BuildId, fixture.IndexId, environment.Build.GameAssemblySha256, ["failed-native-target"], 25),
            "native-tool",
            "1.0.0",
            new string('2', 64),
            NativeRecoveryStatus.Failed,
            [],
            [],
            [],
            false,
            new string('3', 64),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            "Provider exited without producing a bounded evidence packet.");
        await _repository.SaveNativeRecoveryAsync(nativeRecord, TestContext.Current.CancellationToken);

        var result = await CreateService(includeNative: true).InvestigateAsync(
            new SeamInvestigationRequest(
                "Which seam owns settlement clearing?",
                fixture.Target.SymbolId,
                new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
                RelationshipLimit: 3,
                OwnerCandidateLimit: 5,
                SourceContext: 0,
                NativeSymbolIds: ["failed-native-target"],
                NativeTraversalBudget: 25),
            TestContext.Current.CancellationToken);

        var native = Assert.IsType<NativeEvidenceSummary>(result.NativeEvidence);
        Assert.Equal(NativeEvidenceLookupStatus.Matched, native.LookupStatus);
        Assert.Equal(NativeRecoveryStatus.Failed, native.Status);
        Assert.Equal("Provider exited without producing a bounded evidence packet.", native.FailureMessage);
        Assert.Contains(result.CoverageWarnings, warning => warning.Contains("native recovery failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.NextActions,
            action => action.Kind == "targeted-native-recovery" &&
                action.Reason.Contains("Native recovery failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvestigateAsync_bounds_reverse_traversal_to_relationship_limit_per_hop()
    {
        var fixture = await SeedTraversalLimitFixtureAsync(includeSecondOwner: true);
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which owner reaches the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 1),
            RelationshipLimit: 1,
            OwnerCandidateLimit: 5,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.Owner.SymbolId);
        Assert.DoesNotContain(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.SecondOwner!.SymbolId);
    }

    [Fact]
    public async Task InvestigateAsync_marks_owner_coverage_incomplete_when_max_depth_nodes_are_not_expanded()
    {
        var fixture = await SeedDepthBoundedTraversalFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which owner reaches the depth-bounded seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 20),
            RelationshipLimit: 20,
            OwnerCandidateLimit: 20,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.DeepestReachable.SymbolId);
        Assert.DoesNotContain(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.SkippedAtMaxDepth.SymbolId);
        Assert.Contains(result.CoverageWarnings, warning => warning.Contains("owner candidate traversal coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnknownDimensions, value => value.Contains("authority/entity attribution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvestigateAsync_uses_outgoing_method_field_edges_for_field_evidence()
    {
        var fixture = await SeedFieldDirectionFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam reads the state field?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 1),
            RelationshipLimit: 1,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);
        var fields = Assert.Single(result.EvidenceSections, section => section.Family == "FieldReferences");

        Assert.Equal(EvidenceCoverage.Incomplete, fields.Coverage);
        Assert.Equal(2, fields.TotalCount);
        Assert.Equal(1, fields.ReturnedCount);
        Assert.Contains("method-field-read", fields.EvidenceIds);
    }

    [Fact]
    public async Task InvestigateAsync_keeps_reference_field_totals_exact_when_output_is_bounded()
    {
        var fixture = await SeedReferenceFieldFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which reference seam reads the state field?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(
                CodebaseKind.ReferenceMod,
                CodeChannel.Installed,
                Limit: 1,
                Scope: IndexQueryScope.Reference,
                ReferenceCollection: fixture.Collection),
            RelationshipLimit: 1,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);
        var fields = Assert.Single(result.EvidenceSections, section => section.Family == "FieldReferences");

        Assert.Equal(EvidenceCoverage.Incomplete, fields.Coverage);
        Assert.Equal(2, fields.TotalCount);
        Assert.Equal(1, fields.ReturnedCount);
        Assert.Contains("reference-field-read", fields.EvidenceIds);
    }

    [Fact]
    public async Task InvestigateAsync_reports_exact_totals_for_bounded_sections_without_source_neighborhood()
    {
        var fixture = await SeedBoundedEvidenceFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which owners reach the selected type seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 1),
            RelationshipLimit: 1,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        var references = Assert.Single(result.EvidenceSections, section => section.Family == "References");
        Assert.Equal(6, references.TotalCount);
        Assert.Equal(1, references.ReturnedCount);
        Assert.Equal(EvidenceCoverage.Incomplete, references.Coverage);

        var callers = Assert.Single(result.EvidenceSections, section => section.Family == "Callers");
        Assert.Equal(3, callers.TotalCount);
        Assert.Equal(1, callers.ReturnedCount);
        Assert.Equal(EvidenceCoverage.Incomplete, callers.Coverage);

        var callees = Assert.Single(result.EvidenceSections, section => section.Family == "Callees");
        Assert.Equal(3, callees.TotalCount);
        Assert.Equal(1, callees.ReturnedCount);
        Assert.Equal(EvidenceCoverage.Incomplete, callees.Coverage);
    }

    [Fact]
    public async Task InvestigateAsync_retains_federated_owner_endpoint_identity()
    {
        var fixture = await SeedCrossOriginOwnerFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which reference owner reaches the game seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                Limit: 10,
                Scope: IndexQueryScope.All,
                ReferenceCollection: fixture.Collection),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 5,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);
        var referenceOwner = Assert.Single(result.OwnerCandidates, owner => owner.Symbol.SymbolId == fixture.Owner.SymbolId);
        var gameOwner = Assert.Single(result.OwnerCandidates, owner => owner.Symbol.Origin == "game");
        Assert.Equal(fixture.ReferenceIndexId, referenceOwner.Symbol.IndexId);
        Assert.Equal(CodebaseKind.ReferenceMod.ToString(), referenceOwner.Symbol.Codebase);
        Assert.Equal(CodeChannel.Installed.ToString(), referenceOwner.Symbol.Channel);
        Assert.Equal(fixture.Collection, referenceOwner.Symbol.Collection);
        Assert.Equal("index-cross-origin-game", gameOwner.Symbol.IndexId);
        Assert.Equal(CodebaseKind.ScheduleI.ToString(), gameOwner.Symbol.Codebase);
        Assert.Equal(CodeChannel.Installed.ToString(), gameOwner.Symbol.Channel);
    }

    [Fact]
    public async Task InvestigateAsync_populates_gate_fields_and_unknown_classification_for_unresolved_gates()
    {
        var fixture = await SeedRoleFixtureAsync(BodyRecoveryStatus.Recovered, includeCallableSurface: true);
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam owns the request handling path?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 10,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(result.PinnedProvenance);
        Assert.NotNull(result.AuthorityEntityAttribution);
        Assert.NotNull(result.AlternateGenericCallersAndExclusivity);
        Assert.NotNull(result.LifecyclePositionAndBeforeAfterState);
        Assert.NotNull(result.ApiBeforePatchResult);
        Assert.Null(result.PinnedProvenance!.RequestedBuildId);
        Assert.Null(result.PinnedProvenance.ResolvedBuildId);
        Assert.Null(result.PinnedProvenance.ExtractionId);
        Assert.Equal(fixture.IndexId, result.PinnedProvenance.IndexId);
        Assert.False(result.PinnedProvenance.IntegrityVerified);
        var provenanceClaim = Assert.Single(result.Claims, claim => claim.Dimension == "pinned build provenance");
        Assert.Contains("completed index identity", provenanceClaim.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("calling adapter", provenanceClaim.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identifiers are unavailable", provenanceClaim.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Claims, claim => claim.EvidenceClassification == SeamEvidenceClassification.Unknown);
    }

    [Fact]
    public async Task InvestigateAsync_returns_supportable_seam_only_when_all_applicable_gates_are_complete()
    {
        var fixture = await SeedSupportableTypeFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which boundary owns the selected type seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(SeamConclusion.InsufficientCoverage, result.Conclusion);
        Assert.Contains(result.UnknownDimensions, value => value.Contains("lifecycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnknownDimensions, value => value.Contains("api coverage", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ScheduleI:Installed", result.AuthorityEntityAttribution!.Authority);
        Assert.Equal("Game.RequestBoundary.HandleSupportableType", result.AuthorityEntityAttribution.Entity);
        Assert.DoesNotContain(result.Claims, claim => claim.Statement.Contains("multiple owner candidates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvestigateAsync_orders_owner_candidates_deterministically_and_applies_limit_after_sorting()
    {
        var fixture = await SeedDeterministicOrderingFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which upstream owners reach the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 6,
            SourceContext: 0);

        var first = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);
        var second = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Bridge.Mixed..ctor", "Bridge.Alpha.Run", "Bridge.Tie.Run", "Owner.Short.Direct", "Owner.Mixed.Build", "Alpha.Owner.Run"],
            first.OwnerCandidates.Select(candidate => candidate.Symbol.QualifiedName));
        Assert.Equal(
            ["bridge-mixed", "bridge-alpha", "bridge-tie", "owner-short", "owner-mixed", "owner-alpha"],
            first.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId));
        Assert.Equal(
            first.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId),
            second.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId));
        Assert.Equal(
            first.OwnerCandidates.Select(candidate => candidate.Path.RelationshipIds),
            second.OwnerCandidates.Select(candidate => candidate.Path.RelationshipIds));
        Assert.Equal(first.CoverageWarnings, second.CoverageWarnings);
        Assert.Equal(first.UnknownDimensions, second.UnknownDimensions);
        Assert.Equal(first.NextActions.Select(action => action.Kind), second.NextActions.Select(action => action.Kind));
        Assert.DoesNotContain(first.OwnerCandidates, candidate => candidate.Symbol.SymbolId == "tie-b");
        Assert.Equal(2, first.OwnerCandidates[4].Path.SupportingRelationshipFamilyCount);
        Assert.All(first.OwnerCandidates.Take(4), candidate => Assert.Equal(1, candidate.Path.SupportingRelationshipFamilyCount));
        Assert.Equal(1, first.OwnerCandidates[5].Path.SupportingRelationshipFamilyCount);
    }

    [Fact]
    public async Task InvestigateAsync_does_not_prove_exclusivity_from_owner_candidate_limit()
    {
        var fixture = await SeedDeterministicOrderingFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which upstream owner reaches the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 1,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(result.OwnerCandidates);
        Assert.Contains(result.UnknownDimensions, value => value.Contains("exclusivity", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(SeamConclusion.SupportableSeam, result.Conclusion);
    }

    [Fact]
    public async Task InvestigateAsync_distinguishes_incomplete_caller_coverage_from_complete_zero_callee_coverage()
    {
        var fixture = await SeedOc32FixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam owns settlement clearing?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 3),
            RelationshipLimit: 3,
            OwnerCandidateLimit: 5,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        var callers = Assert.Single(result.EvidenceSections, section => section.Family == "Callers");
        var callees = Assert.Single(result.EvidenceSections, section => section.Family == "Callees");

        Assert.Equal(EvidenceCoverage.Incomplete, callers.Coverage);
        Assert.Equal(EvidenceCoverage.Complete, callees.Coverage);
        Assert.Equal(0, callees.TotalCount);
        Assert.Equal(0, callees.ReturnedCount);
    }

    [Fact]
    public async Task InvestigateAsync_keeps_incoming_owner_when_outgoing_references_consume_relationship_limit()
    {
        var fixture = await SeedTraversalLimitFixtureAsync();
        await SeedReferenceCollectionAsync(fixture.IndexId, fixture.BuildId);
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which owner reaches the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                Limit: 1,
                Scope: IndexQueryScope.All,
                ReferenceCollection: "reference-collection"),
            RelationshipLimit: 1,
            OwnerCandidateLimit: 5,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.Owner.SymbolId);
    }

    [Fact]
    public async Task InvestigateAsync_orders_equal_paths_by_persisted_canonical_key_before_symbol_id()
    {
        var fixture = await SeedCanonicalOrderingFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which upstream owners reach the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 5),
            RelationshipLimit: 5,
            OwnerCandidateLimit: 2,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(["owner-a", "owner-z"], result.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId));
    }

    [Fact]
    public async Task InvestigateAsync_retains_reachable_generic_callers_with_their_shortest_path()
    {
        var fixture = await SeedExpandedCandidateFixtureAsync();
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which owners reach the selected seam?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 5),
            RelationshipLimit: 5,
            OwnerCandidateLimit: 5,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        var generic = Assert.Single(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.Generic.SymbolId);
        Assert.Equal(["generic-to-target"], generic.Path.RelationshipIds);
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Symbol.SymbolId == fixture.Outer.SymbolId);
    }

    [Fact]
    public async Task InvestigateAsync_uses_callable_surface_from_resolved_pinned_game_index_when_request_codebase_is_reference()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-pinned-callable");

        var pinnedTarget = Method("target-pinned-callable", "snapshot-pinned-callable", "Game.Seams.PinnedCallableTarget.Run", BodyRecoveryStatus.Recovered);
        await CompleteGameRunAsync(
            "build-pinned-callable",
            "index-pinned-callable",
            "snapshot-pinned-callable",
            pinnedTarget,
            [],
            [],
            includeCallableSurface: true,
            completedAtUtc: "2026-08-29T00:01:00Z");

        await SeedEnvironmentAsync("build-latest-callable");
        var latestTarget = Method("target-latest-callable", "snapshot-latest-callable", "Game.Seams.PinnedCallableTarget.Run", BodyRecoveryStatus.Recovered);
        await CompleteGameRunAsync(
            "build-latest-callable",
            "index-latest-callable",
            "snapshot-latest-callable",
            latestTarget,
            [],
            [],
            includeCallableSurface: false,
            completedAtUtc: "2026-08-29T00:02:00Z");

        await SeedReferenceCollectionAsync("index-pinned-callable", "build-pinned-callable");
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which callable surface belongs to the pinned seam?",
            pinnedTarget.SymbolId,
            new IndexQueryOptions(
                CodebaseKind.ReferenceMod,
                CodeChannel.Installed,
                Limit: 5,
                Scope: IndexQueryScope.All,
                ReferenceCollection: "reference-collection"),
            RelationshipLimit: 5,
            OwnerCandidateLimit: 5,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("index-pinned-callable", result.Resolution.Symbol?.IndexId);
        Assert.Equal(EvidenceCoverage.Complete, result.CallableCoverage);
        Assert.Equal(EvidenceCoverage.Unavailable, result.ApiBeforePatchResult!.Coverage);
        Assert.Equal("UNKNOWN", result.ApiBeforePatchResult.ApiSurface);
        Assert.Contains(result.UnknownDimensions, value => value.Contains("api coverage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NextActions, action => action.Kind == "api-lookup");
    }

    [Fact]
    public async Task InvestigateAsync_projects_verbose_evidence_only_when_details_are_requested()
    {
        var fixture = await SeedRoleFixtureAsync(BodyRecoveryStatus.Recovered, includeCallableSurface: true);
        var service = CreateService();
        var summaryRequest = new SeamInvestigationRequest(
            "Which seam owns the request handling path?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 10,
            SourceContext: 0,
            IncludeDetails: false);

        var summary = await service.InvestigateAsync(summaryRequest, TestContext.Current.CancellationToken);
        var details = await service.InvestigateAsync(summaryRequest with { IncludeDetails = true }, TestContext.Current.CancellationToken);

        Assert.Empty(summary.Claims);
        Assert.Empty(summary.EvidenceSections);
        Assert.NotEmpty(details.Claims);
        Assert.NotEmpty(details.EvidenceSections);

        Assert.Equal(details.BehavioralQuestion, summary.BehavioralQuestion);
        Assert.Equal(details.Conclusion, summary.Conclusion);
        Assert.Equal(details.Resolution, summary.Resolution);
        Assert.Equal(details.Candidate, summary.Candidate);
        Assert.Equal(details.CandidateRole, summary.CandidateRole);
        Assert.Equal(details.BodyRecoveryStatus, summary.BodyRecoveryStatus);
        Assert.Equal(details.BodyCoverage, summary.BodyCoverage);
        Assert.Equal(details.CallableCoverage, summary.CallableCoverage);
        Assert.Equal(
            details.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId),
            summary.OwnerCandidates.Select(candidate => candidate.Symbol.SymbolId));
        Assert.Equal(
            details.OwnerCandidates.Select(candidate => candidate.Path.RelationshipIds),
            summary.OwnerCandidates.Select(candidate => candidate.Path.RelationshipIds));
        Assert.Equal(
            details.OwnerCandidates.Select(candidate => candidate.EvidenceIds),
            summary.OwnerCandidates.Select(candidate => candidate.EvidenceIds));
        Assert.Equal(details.CoverageWarnings, summary.CoverageWarnings);
        Assert.Equal(details.UnknownDimensions, summary.UnknownDimensions);
        Assert.Equal(details.NextActions, summary.NextActions);
        Assert.Equal(details.PinnedProvenance, summary.PinnedProvenance);
        Assert.Equal(details.AuthorityEntityAttribution!.Authority, summary.AuthorityEntityAttribution!.Authority);
        Assert.Equal(details.AuthorityEntityAttribution.Entity, summary.AuthorityEntityAttribution.Entity);
        Assert.Equal(details.AuthorityEntityAttribution.EvidenceIds, summary.AuthorityEntityAttribution.EvidenceIds);
        Assert.Equal(details.AlternateGenericCallersAndExclusivity!.IsExclusive, summary.AlternateGenericCallersAndExclusivity!.IsExclusive);
        Assert.Equal(details.AlternateGenericCallersAndExclusivity.Coverage, summary.AlternateGenericCallersAndExclusivity.Coverage);
        Assert.Equal(details.AlternateGenericCallersAndExclusivity.EvidenceIds, summary.AlternateGenericCallersAndExclusivity.EvidenceIds);
        Assert.Equal(details.LifecyclePositionAndBeforeAfterState!.Position, summary.LifecyclePositionAndBeforeAfterState!.Position);
        Assert.Equal(details.LifecyclePositionAndBeforeAfterState.BeforeState, summary.LifecyclePositionAndBeforeAfterState.BeforeState);
        Assert.Equal(details.LifecyclePositionAndBeforeAfterState.AfterState, summary.LifecyclePositionAndBeforeAfterState.AfterState);
        Assert.Equal(details.LifecyclePositionAndBeforeAfterState.Coverage, summary.LifecyclePositionAndBeforeAfterState.Coverage);
        Assert.Equal(details.LifecyclePositionAndBeforeAfterState.EvidenceIds, summary.LifecyclePositionAndBeforeAfterState.EvidenceIds);
        Assert.Equal(details.ApiBeforePatchResult!.ApiSurface, summary.ApiBeforePatchResult!.ApiSurface);
        Assert.Equal(details.ApiBeforePatchResult.Result, summary.ApiBeforePatchResult.Result);
        Assert.Equal(details.ApiBeforePatchResult.Coverage, summary.ApiBeforePatchResult.Coverage);
        Assert.Equal(details.ApiBeforePatchResult.EvidenceIds, summary.ApiBeforePatchResult.EvidenceIds);
    }

    [Fact]
    public async Task InvestigateAsync_classifies_roles_and_records_unknown_dimensions_without_unknown_claim_classes()
    {
        var fixture = await SeedRoleFixtureAsync(BodyRecoveryStatus.Recovered, includeCallableSurface: true);
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam owns the request handling path?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 10,
            SourceContext: 0,
            IncludeDetails: true);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("request", result.CandidateRole);
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Role == "request");
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Role == "presentation");
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Role == "cleanup");
        Assert.Contains(result.OwnerCandidates, candidate => candidate.Role == "unknown");
        Assert.Contains(result.UnknownDimensions, value => value.Contains("authority", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnknownDimensions, value => value.Contains("api coverage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.UnknownDimensions, value => value.Contains("native substrate", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("UNKNOWN", result.AuthorityEntityAttribution!.Authority);
        Assert.Equal("UNKNOWN", result.AuthorityEntityAttribution.Entity);
        Assert.Contains(result.Claims, claim => claim.EvidenceClassification == SeamEvidenceClassification.Unknown);
    }

    [Fact]
    public async Task InvestigateAsync_selects_only_bounded_next_actions_when_body_coverage_is_stubbed()
    {
        var fixture = await SeedRoleFixtureAsync(BodyRecoveryStatus.StubOrUnavailable, includeCallableSurface: false);
        var service = CreateService();
        var request = new SeamInvestigationRequest(
            "Which seam owns the request handling path?",
            fixture.Target.SymbolId,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            RelationshipLimit: 10,
            OwnerCandidateLimit: 10,
            SourceContext: 0);

        var result = await service.InvestigateAsync(request, TestContext.Current.CancellationToken);
        var actionKinds = result.NextActions.Select(action => action.Kind).ToArray();

        Assert.Contains("targeted-native-recovery", actionKinds);
        Assert.Contains("runtime-proof", actionKinds);
        Assert.Contains("api-lookup", actionKinds);
        Assert.All(
            actionKinds,
            kind => Assert.Contains(kind, new[] { "api-lookup", "targeted-native-recovery", "runtime-proof", "qualify-symbol" }));
        Assert.DoesNotContain(result.NextActions, action => action.Kind.Contains("implementation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Seam_investigation_result_can_represent_a_resolved_no_supportable_seam_packet()
    {
        var candidate = Symbol(
            symbolId: "ScheduleI:Installed:Method:Demo.Component::HandleTrigger()",
            qualifiedName: "Demo.Component.HandleTrigger",
            signature: "System.Void Demo.Component::HandleTrigger()");
        var resolution = new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidate, []);
        var path = new SeamEvidencePath(
            ["relationship-call", "relationship-read", "relationship-call-2"],
            3,
            2);
        var ownerCandidate = new SeamOwnerCandidate(candidate, "Observer", path, ["relationship-call", "relationship-read"]);
        var bodySection = new SeamEvidenceSection(
            "Body",
            EvidenceCoverage.Complete,
            0,
            0,
            [],
            null);

        var result = new SeamInvestigationResult(
            "Which owner handles trigger-driven inventory refreshes?",
            SeamConclusion.NoSupportableSeam,
            resolution,
            candidate,
            "Observer",
            BodyRecoveryStatus.Recovered,
            EvidenceCoverage.Bounded,
            EvidenceCoverage.Complete,
            [new SeamEvidenceClaim("Ownership", "DERIVED", "Observed call sites point to multiple unrelated owners.", ["relationship-call"])],
            [bodySection],
            [ownerCandidate],
            ["Body evidence was bounded by the selected source context."],
            ["Lifecycle"],
            [new SeamNextAction("RuntimeProbe", "Static evidence conflicts across owners.", "Trigger flow", true)]);

        Assert.Equal(SeamConclusion.NoSupportableSeam, result.Conclusion);
        Assert.Same(candidate, result.Candidate);
        Assert.Equal("Observer", result.CandidateRole);
        Assert.Equal(["relationship-call", "relationship-read", "relationship-call-2"], result.OwnerCandidates[0].Path.RelationshipIds);
        Assert.Equal(2, result.OwnerCandidates[0].Path.SupportingRelationshipFamilyCount);
        Assert.Equal(EvidenceCoverage.Bounded, result.BodyCoverage);
        Assert.Equal(EvidenceCoverage.Complete, result.CallableCoverage);
        Assert.Equal(EvidenceCoverage.Complete, result.EvidenceSections[0].Coverage);
        Assert.Equal(0, result.EvidenceSections[0].TotalCount);
        Assert.Equal(0, result.EvidenceSections[0].ReturnedCount);
    }

    [Fact]
    public void Evidence_coverage_incomplete_is_distinct_from_a_complete_zero_count_section()
    {
        var incomplete = new SeamEvidenceSection(
            "Callers",
            EvidenceCoverage.Incomplete,
            12,
            4,
            ["rel-1", "rel-2", "rel-3", "rel-4"],
            "Results were truncated before the full caller set was recovered.");
        var completeButEmpty = new SeamEvidenceSection(
            "Callees",
            EvidenceCoverage.Complete,
            0,
            0,
            [],
            null);

        Assert.NotEqual(incomplete.Coverage, completeButEmpty.Coverage);
        Assert.Equal(EvidenceCoverage.Incomplete, incomplete.Coverage);
        Assert.Equal(EvidenceCoverage.Complete, completeButEmpty.Coverage);
        Assert.Equal(0, completeButEmpty.TotalCount);
        Assert.Equal(0, completeButEmpty.ReturnedCount);
    }

    [Fact]
    public void Seam_investigation_result_exposes_each_at33_gate_dimension_explicitly()
    {
        var candidate = Symbol(
            symbolId: "candidate-1",
            qualifiedName: "Demo.RequestBoundary.Handle",
            signature: "System.Void Demo.RequestBoundary::Handle()");
        var resolution = new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidate, []);
        var ownerCandidate = new SeamOwnerCandidate(
            candidate,
            "request",
            new SeamEvidencePath(["caller-1"], 1, 1),
            ["caller-1"]);
        var pinnedProvenance = new SeamPinnedProvenance(
            RequestedBuildId: "requested-build",
            ResolvedBuildId: "resolved-build",
            ExtractionId: "extraction-1",
            IndexId: "index-1",
            Codebase: "ScheduleI",
            Channel: "Installed",
            IntegrityVerified: true);
        var authorityEntityAttribution = new SeamAuthorityEntityAttribution(
            "installed-build-authority",
            "settlement-entity",
            ["authority-1"]);
        var alternateCallersAndExclusivity = new SeamAlternateGenericCallerEvidence(
            [ownerCandidate],
            IsExclusive: false,
            EvidenceCoverage.Bounded,
            ["caller-1"]);
        var lifecycle = new SeamLifecyclePositionAndBeforeAfterState(
            "state-writer",
            "inventory-open",
            "inventory-cleared",
            EvidenceCoverage.Incomplete,
            ["lifecycle-1"]);
        var apiBeforePatch = new SeamApiBeforePatchResult(
            "S1API",
            "operation is not exposed by the inspected API surface",
            EvidenceCoverage.Complete,
            ["api-1"]);

        var result = new SeamInvestigationResult(
            "Which seam owns settlement clearing?",
            SeamConclusion.NoSupportableSeam,
            resolution,
            candidate,
            "request",
            BodyRecoveryStatus.Recovered,
            EvidenceCoverage.Complete,
            EvidenceCoverage.Complete,
            [],
            [],
            [ownerCandidate],
            [],
            ["lifecycle position and before/after state"],
            [],
            pinnedProvenance,
            authorityEntityAttribution,
            alternateCallersAndExclusivity,
            lifecycle,
            apiBeforePatch);

        Assert.Same(pinnedProvenance, result.PinnedProvenance);
        Assert.Same(authorityEntityAttribution, result.AuthorityEntityAttribution);
        Assert.Same(alternateCallersAndExclusivity, result.AlternateGenericCallersAndExclusivity);
        Assert.Same(lifecycle, result.LifecyclePositionAndBeforeAfterState);
        Assert.Same(apiBeforePatch, result.ApiBeforePatchResult);
    }

    [Fact]
    public void Seam_evidence_claim_uses_only_closed_fact_derived_or_unknown_classifications()
    {
        Assert.Equal(
            [SeamEvidenceClassification.Fact, SeamEvidenceClassification.Derived, SeamEvidenceClassification.Unknown],
            Enum.GetValues<SeamEvidenceClassification>());

        var claim = new SeamEvidenceClaim(
            "authority/entity attribution",
            SeamEvidenceClassification.Unknown,
            "The owning entity remains unresolved.",
            []);

        Assert.Equal(SeamEvidenceClassification.Unknown, claim.EvidenceClassification);
        Assert.Equal("UNKNOWN", claim.Classification);
        Assert.Throws<ArgumentException>(() => new SeamEvidenceClaim(
            "authority/entity attribution",
            "Contradictory",
            "The owning entity is contradictory.",
            []));
        Assert.Throws<ArgumentException>(() => claim with { Classification = "INTERPRETATION" });
    }

    [Theory]
    [InlineData(null, "Demo.Component.HandleTrigger", 1, 1, 0, "BehavioralQuestion")]
    [InlineData("", "Demo.Component.HandleTrigger", 1, 1, 0, "BehavioralQuestion")]
    [InlineData("   ", "Demo.Component.HandleTrigger", 1, 1, 0, "BehavioralQuestion")]
    [InlineData("Question", null, 1, 1, 0, "Selector")]
    [InlineData("Question", "", 1, 1, 0, "Selector")]
    [InlineData("Question", "   ", 1, 1, 0, "Selector")]
    [InlineData("Question", "Demo.Component.HandleTrigger", 0, 1, 0, "RelationshipLimit")]
    [InlineData("Question", "Demo.Component.HandleTrigger", 51, 1, 0, "RelationshipLimit")]
    [InlineData("Question", "Demo.Component.HandleTrigger", 1, 0, 0, "OwnerCandidateLimit")]
    [InlineData("Question", "Demo.Component.HandleTrigger", 1, 51, 0, "OwnerCandidateLimit")]
    [InlineData("Question", "Demo.Component.HandleTrigger", 1, 1, -1, "SourceContext")]
    public void Seam_investigation_request_rejects_invalid_arguments(
        string? behavioralQuestion,
        string? selector,
        int relationshipLimit,
        int ownerCandidateLimit,
        int sourceContext,
        string expectedParameter)
    {
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed);

        var exception = Assert.ThrowsAny<ArgumentException>(() => new SeamInvestigationRequest(
            behavioralQuestion!,
            selector!,
            options,
            relationshipLimit,
            ownerCandidateLimit,
            sourceContext));

        Assert.Equal(expectedParameter, exception.ParamName);
    }

    [Fact]
    public void Seam_investigation_request_preserves_valid_arguments_and_defaults()
    {
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 25);

        var request = new SeamInvestigationRequest(
            "Which seam owns inventory refresh behavior?",
            "Demo.Inventory.RefreshOwner",
            options);

        Assert.Equal("Which seam owns inventory refresh behavior?", request.BehavioralQuestion);
        Assert.Equal("Demo.Inventory.RefreshOwner", request.Selector);
        Assert.Equal(options, request.Options);
        Assert.Equal(50, request.RelationshipLimit);
        Assert.Equal(10, request.OwnerCandidateLimit);
        Assert.Equal(5, request.SourceContext);
        Assert.False(request.IncludeDetails);
    }

    [Fact]
    public void Seam_investigation_request_rejects_invalid_object_initializer_and_with_mutation()
    {
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 25);
        var request = new SeamInvestigationRequest(
            "Which seam owns inventory refresh behavior?",
            "Demo.Inventory.RefreshOwner",
            options);

        var initializerException = Assert.Throws<ArgumentException>(() => new SeamInvestigationRequest(
            "Which seam owns inventory refresh behavior?",
            "Demo.Inventory.RefreshOwner",
            options)
        {
            Selector = ""
        });
        var withException = Assert.Throws<ArgumentOutOfRangeException>(() => request with
        {
            RelationshipLimit = 0
        });

        Assert.Equal("Selector", initializerException.ParamName);
        Assert.Equal("RelationshipLimit", withException.ParamName);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private SeamInvestigationService CreateService(bool includeNative = false) =>
        new(
            new IndexQueryService(_repository, _dataRoot),
            new FederatedIndexQueryService(_repository, _dataRoot),
            new ReferenceModQueryService(_repository, _dataRoot),
            includeNative ? _repository : null,
            includeNative ? _repository : null);

    private async Task<MethodFixture> SeedOc32FixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-oc32");

        var target = Method("target-oc32", "snapshot-oc32", "Game.Seams.Target.Run", BodyRecoveryStatus.Recovered);
        var type = Type("type-free-server", "snapshot-oc32", "Game.Free_Server");
        var requestBoundary = Method("request-boundary", "snapshot-oc32", "Game.RequestBoundary.HandleSettlementRequest", BodyRecoveryStatus.Recovered);
        var genericClearing = Method("generic-clearing", "snapshot-oc32", "Game.Clearing.ClearGeneric", BodyRecoveryStatus.Recovered);
        var freeRelease = Method("free-release", "snapshot-oc32", "Game.Free_Server.Release", BodyRecoveryStatus.Recovered);
        var uiSettlement = Method("ui-settlement", "snapshot-oc32", "UI.SettlementPanel.ApplySettlementWithoutPlayer", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            "build-oc32",
            "index-oc32",
            "snapshot-oc32",
            target,
            [
                type,
                requestBoundary,
                genericClearing,
                freeRelease,
                uiSettlement
            ],
            [
                Edge("caller-001-request", "snapshot-oc32", requestBoundary.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-002-generic-clear", "snapshot-oc32", genericClearing.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-003-free-release", "snapshot-oc32", freeRelease.SymbolId, target.SymbolId, null, "Calls"),
                Edge("caller-004-ui-settlement", "snapshot-oc32", uiSettlement.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new MethodFixture("build-oc32", "index-oc32", target);
    }

    private async Task<MethodFixture> SeedRoleFixtureAsync(
        BodyRecoveryStatus bodyRecoveryStatus,
        bool includeCallableSurface)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-role");

        var target = Method("target-role", "snapshot-role", "Game.Seams.RoleTarget.Run", bodyRecoveryStatus);
        var request = Method("role-request", "snapshot-role", "Alpha.RequestBoundary.OpenRequest", BodyRecoveryStatus.Recovered);
        var presentation = Method("role-presentation", "snapshot-role", "UI.SettlementPanel.RefreshWithoutPlayer", BodyRecoveryStatus.Recovered);
        var cleanup = Method("role-cleanup", "snapshot-role", "Zulu.Free_Server.Release", BodyRecoveryStatus.Recovered);
        var unknown = Method("role-unknown", "snapshot-role", "Omega.GenericCoordinator.Dispatch", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            "build-role",
            "index-role",
            "snapshot-role",
            target,
            [request, presentation, cleanup, unknown],
            [
                Edge("role-001-request", "snapshot-role", request.SymbolId, target.SymbolId, null, "Calls"),
                Edge("role-002-presentation", "snapshot-role", presentation.SymbolId, target.SymbolId, null, "Calls"),
                Edge("role-003-cleanup", "snapshot-role", cleanup.SymbolId, target.SymbolId, null, "Calls"),
                Edge("role-004-unknown", "snapshot-role", unknown.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface);

        return new MethodFixture("build-role", "index-role", target);
    }

    private async Task<TraversalLimitFixture> SeedTraversalLimitFixtureAsync(bool includeSecondOwner = false)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-traversal-limit");

        var target = Method("target-traversal-limit", "snapshot-traversal-limit", "Game.Seams.TraversalLimitTarget.Run", BodyRecoveryStatus.Recovered);
        var owner = Method("owner-traversal-limit", "snapshot-traversal-limit", "Game.RequestBoundary.HandleTraversalLimit", BodyRecoveryStatus.Recovered);
        var secondOwner = includeSecondOwner
            ? Method("owner-traversal-limit-second", "snapshot-traversal-limit", "Game.RequestBoundary.HandleTraversalLimitSecond", BodyRecoveryStatus.Recovered)
            : null;
        var additionalSymbols = secondOwner is null ? [owner] : new[] { owner, secondOwner };
        var relationships = new List<IndexRelationshipRecord>
        {
            Edge("edge-001-outgoing", "snapshot-traversal-limit", target.SymbolId, null, "External.Target::Run()", "Calls"),
            Edge("edge-002-incoming", "snapshot-traversal-limit", owner.SymbolId, target.SymbolId, null, "Calls")
        };
        if (secondOwner is not null)
            relationships.Add(Edge("edge-999-incoming-second", "snapshot-traversal-limit", secondOwner.SymbolId, target.SymbolId, null, "Calls"));

        await CompleteGameRunAsync(
            "build-traversal-limit",
            "index-traversal-limit",
            "snapshot-traversal-limit",
            target,
            additionalSymbols,
            relationships,
            includeCallableSurface: true);

        return new TraversalLimitFixture("build-traversal-limit", "index-traversal-limit", target, owner, secondOwner);
    }

    private async Task<DepthBoundedTraversalFixture> SeedDepthBoundedTraversalFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-depth-bounded");

        var target = Method("target-depth-bounded", "snapshot-depth-bounded", "Game.Seams.DepthBoundedTarget.Run", BodyRecoveryStatus.Recovered);
        var owner = Method("owner-depth-bounded", "snapshot-depth-bounded", "Game.RequestBoundary.HandleDepthBounded", BodyRecoveryStatus.Recovered);
        var bridge2 = Method("bridge-depth-bounded-2", "snapshot-depth-bounded", "Game.Clearing.DepthBridge2", BodyRecoveryStatus.Recovered);
        var bridge3 = Method("bridge-depth-bounded-3", "snapshot-depth-bounded", "Game.Clearing.DepthBridge3", BodyRecoveryStatus.Recovered);
        var deepestReachable = Method("bridge-depth-bounded-4", "snapshot-depth-bounded", "Game.Clearing.DepthBridge4", BodyRecoveryStatus.Recovered);
        var skippedAtMaxDepth = Method("owner-depth-bounded-skipped", "snapshot-depth-bounded", "Game.RequestBoundary.HandleBeyondDepth", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            "build-depth-bounded",
            "index-depth-bounded",
            "snapshot-depth-bounded",
            target,
            [owner, bridge2, bridge3, deepestReachable, skippedAtMaxDepth],
            [
                Edge("depth-001-owner", "snapshot-depth-bounded", owner.SymbolId, target.SymbolId, null, "Calls"),
                Edge("depth-002-bridge", "snapshot-depth-bounded", bridge2.SymbolId, owner.SymbolId, null, "Calls"),
                Edge("depth-003-bridge", "snapshot-depth-bounded", bridge3.SymbolId, bridge2.SymbolId, null, "Calls"),
                Edge("depth-004-bridge", "snapshot-depth-bounded", deepestReachable.SymbolId, bridge3.SymbolId, null, "Calls"),
                Edge("depth-005-skipped", "snapshot-depth-bounded", skippedAtMaxDepth.SymbolId, deepestReachable.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new DepthBoundedTraversalFixture(target, deepestReachable, skippedAtMaxDepth);
    }

    private async Task<FieldDirectionFixture> SeedFieldDirectionFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-field-direction");

        var target = Method("target-field-direction", "snapshot-field-direction", "Game.Seams.FieldDirectionTarget.Run", BodyRecoveryStatus.Recovered);
        var owner = Method("owner-field-direction", "snapshot-field-direction", "Game.RequestBoundary.HandleFieldDirection", BodyRecoveryStatus.Recovered);
        var field = new IndexSymbolRecord(
            "field-field-direction",
            "snapshot-field-direction",
            "ScheduleI:Installed:Field:Game.State::Value",
            "Field",
            "Game.State.Value",
            "System.Int32 Game.State::Value",
            false,
            null);

        await CompleteGameRunAsync(
            "build-field-direction",
            "index-field-direction",
            "snapshot-field-direction",
            target,
            [owner, field],
            [
                Edge("method-field-read", "snapshot-field-direction", target.SymbolId, field.SymbolId, "Game.State::Value", "ReadsField"),
                Edge("method-field-write", "snapshot-field-direction", target.SymbolId, field.SymbolId, "Game.State::Value", "WritesField"),
                Edge("field-direction-owner", "snapshot-field-direction", owner.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new FieldDirectionFixture(target);
    }

    private async Task<ReferenceFieldFixture> SeedReferenceFieldFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-reference-fields");

        var gameTarget = Method("game-reference-fields-anchor", "snapshot-reference-fields-game", "Game.Seams.ReferenceFieldsAnchor.Run", BodyRecoveryStatus.Recovered);
        await CompleteGameRunAsync(
            "build-reference-fields",
            "index-reference-fields-game",
            "snapshot-reference-fields-game",
            gameTarget,
            [],
            [],
            includeCallableSurface: false);

        var collection = "reference-fields-collection";
        var snapshot = new CodeSnapshotRecord(
            "snapshot-reference-fields-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            collection,
            "2026-08-29T00:02:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var run = new IndexRunRecord(
            "index-reference-fields-reference",
            snapshot.SnapshotId,
            IndexRunStatus.Running,
            snapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(run, TestContext.Current.CancellationToken);

        var target = new IndexSymbolRecord(
            "reference-field-target",
            snapshot.SnapshotId,
            "ReferenceMod:Installed:Method:qol.Seams.ReferenceFieldTarget::Run()",
            "Method",
            "qol.Seams.ReferenceFieldTarget.Run",
            "System.Void qol.Seams.ReferenceFieldTarget::Run()",
            false,
            BodyRecoveryStatus.Recovered);
        var field = new IndexSymbolRecord(
            "reference-field-state",
            snapshot.SnapshotId,
            "ReferenceMod:Installed:Field:qol.State::Value",
            "Field",
            "qol.State.Value",
            "System.Int32 qol.State::Value",
            false,
            null);
        var owner = new IndexSymbolRecord(
            "reference-field-owner",
            snapshot.SnapshotId,
            "ReferenceMod:Installed:Method:qol.RequestBoundary::Handle()",
            "Method",
            "qol.RequestBoundary.Handle",
            "System.Void qol.RequestBoundary::Handle()",
            false,
            BodyRecoveryStatus.Recovered);

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [target, field, owner],
                [],
                [],
                [],
                [
                    Edge("reference-field-read", snapshot.SnapshotId, target.SymbolId, field.SymbolId, "qol.State::Value", "ReadsField"),
                    Edge("reference-field-write", snapshot.SnapshotId, target.SymbolId, field.SymbolId, "qol.State::Value", "WritesField"),
                    Edge("reference-field-owner-edge", snapshot.SnapshotId, owner.SymbolId, target.SymbolId, null, "Calls")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, "index-reference-fields-game", "build-reference-fields"),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        "mods/qol",
                        "qol-reference-fields",
                        [target.SymbolId, field.SymbolId, owner.SymbolId])
                ]),
            "2026-08-29T00:03:00Z",
            TestContext.Current.CancellationToken);

        return new ReferenceFieldFixture(target, collection);
    }

    private async Task<CrossOriginOwnerFixture> SeedCrossOriginOwnerFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-cross-origin");

        var target = Method("target-cross-origin", "snapshot-cross-origin", "Game.Seams.CrossOriginTarget.Run", BodyRecoveryStatus.Recovered);
        var gameOwner = Method("game-owner-cross-origin", "snapshot-cross-origin", "Game.RequestBoundary.HandleCrossOrigin", BodyRecoveryStatus.Recovered);
        await CompleteGameRunAsync(
            "build-cross-origin",
            "index-cross-origin-game",
            "snapshot-cross-origin",
            target,
            [gameOwner],
            [Edge("game-cross-origin-owner", "snapshot-cross-origin", gameOwner.SymbolId, target.SymbolId, null, "Calls")],
            includeCallableSurface: true);

        var collection = "cross-origin-collection";
        var referenceSnapshot = new CodeSnapshotRecord(
            "snapshot-cross-origin-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            collection,
            "2026-08-29T00:02:00Z");
        await _repository.CreateCodeSnapshotAsync(referenceSnapshot, TestContext.Current.CancellationToken);
        var referenceRun = new IndexRunRecord(
            "index-cross-origin-reference",
            referenceSnapshot.SnapshotId,
            IndexRunStatus.Running,
            referenceSnapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(referenceRun, TestContext.Current.CancellationToken);
        var owner = Method("owner-cross-origin", referenceSnapshot.SnapshotId, "qol.RequestBoundary.HandleCrossOrigin", BodyRecoveryStatus.Recovered);
        await _repository.CompleteIndexRunAsync(
            referenceRun.IndexId,
            new IndexWriteSet(
                [owner],
                [],
                [],
                [],
                [Edge("cross-origin-owner", referenceSnapshot.SnapshotId, owner.SymbolId, target.SymbolId, null, "Calls")],
                ReferenceIndexContext: new ReferenceIndexContextRecord(referenceRun.IndexId, "index-cross-origin-game", "build-cross-origin"),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        "mods/qol",
                        "qol-cross-origin",
                        [owner.SymbolId])
                ]),
            "2026-08-29T00:03:00Z",
            TestContext.Current.CancellationToken);

        return new CrossOriginOwnerFixture(target, owner, collection, referenceRun.IndexId);
    }

    private async Task<SupportableTypeFixture> SeedSupportableTypeFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-supportable-type");

        var target = Type("target-supportable-type", "snapshot-supportable-type", "Game.Seams.SupportableType");
        var owner = Method("owner-supportable-type", "snapshot-supportable-type", "Game.RequestBoundary.HandleSupportableType", BodyRecoveryStatus.Recovered);
        await CompleteGameRunAsync(
            "build-supportable-type",
            "index-supportable-type",
            "snapshot-supportable-type",
            target,
            [owner],
            [Edge("supportable-type-owner", "snapshot-supportable-type", owner.SymbolId, target.SymbolId, null, "Calls")],
            includeCallableSurface: false);

        return new SupportableTypeFixture(target);
    }

    private async Task<BoundedEvidenceFixture> SeedBoundedEvidenceFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-bounded-evidence");

        var target = Type("target-bounded-evidence", "snapshot-bounded-evidence", "Game.Seams.BoundedEvidenceTarget");
        var owners = new[]
        {
            Method("owner-bounded-evidence-1", "snapshot-bounded-evidence", "Game.RequestBoundary.HandleFirst", BodyRecoveryStatus.Recovered),
            Method("owner-bounded-evidence-2", "snapshot-bounded-evidence", "Game.RequestBoundary.HandleSecond", BodyRecoveryStatus.Recovered),
            Method("owner-bounded-evidence-3", "snapshot-bounded-evidence", "Game.RequestBoundary.HandleThird", BodyRecoveryStatus.Recovered)
        };
        var callees = new[]
        {
            Method("callee-bounded-evidence-1", "snapshot-bounded-evidence", "Game.Clearing.ClearFirst", BodyRecoveryStatus.Recovered),
            Method("callee-bounded-evidence-2", "snapshot-bounded-evidence", "Game.Clearing.ClearSecond", BodyRecoveryStatus.Recovered),
            Method("callee-bounded-evidence-3", "snapshot-bounded-evidence", "Game.Clearing.ClearThird", BodyRecoveryStatus.Recovered)
        };

        await CompleteGameRunAsync(
            "build-bounded-evidence",
            "index-bounded-evidence",
            "snapshot-bounded-evidence",
            target,
            owners.Concat(callees).ToArray(),
            owners
                .Select((owner, index) => Edge(
                    $"bounded-evidence-owner-{index + 1}",
                    "snapshot-bounded-evidence",
                    owner.SymbolId,
                    target.SymbolId,
                    null,
                    "Calls"))
                .Concat(callees.Select((callee, index) => Edge(
                    $"bounded-evidence-callee-{index + 1}",
                    "snapshot-bounded-evidence",
                    target.SymbolId,
                    callee.SymbolId,
                    null,
                    "Calls")))
                .ToArray(),
            includeCallableSurface: false);

        return new BoundedEvidenceFixture(target);
    }

    private async Task<MethodFixture> SeedCanonicalOrderingFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-canonical-order");

        var target = Method("target-canonical-order", "snapshot-canonical-order", "Game.Seams.CanonicalOrderTarget.Run", BodyRecoveryStatus.Recovered);
        var ownerZ = Method(
            "owner-z",
            "snapshot-canonical-order",
            "A.Proxy.Owner",
            BodyRecoveryStatus.Recovered,
            canonicalMember: "Zed.Owner::Run()");
        var ownerA = Method(
            "owner-a",
            "snapshot-canonical-order",
            "Z.Proxy.Owner",
            BodyRecoveryStatus.Recovered,
            canonicalMember: "Alpha.Owner::Run()");

        await CompleteGameRunAsync(
            "build-canonical-order",
            "index-canonical-order",
            "snapshot-canonical-order",
            target,
            [ownerZ, ownerA],
            [
                Edge("canonical-001-owner-z", "snapshot-canonical-order", ownerZ.SymbolId, target.SymbolId, null, "Calls"),
                Edge("canonical-002-owner-a", "snapshot-canonical-order", ownerA.SymbolId, target.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new MethodFixture("build-canonical-order", "index-canonical-order", target);
    }

    private async Task<ExpandedCandidateFixture> SeedExpandedCandidateFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-expanded-candidate");

        var target = Method("target-expanded-candidate", "snapshot-expanded-candidate", "Game.Seams.ExpandedCandidateTarget.Run", BodyRecoveryStatus.Recovered);
        var generic = Method("generic-expanded-candidate", "snapshot-expanded-candidate", "Game.Clearing.ClearGeneric", BodyRecoveryStatus.Recovered);
        var outer = Method("outer-expanded-candidate", "snapshot-expanded-candidate", "Game.RequestBoundary.HandleClear", BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            "build-expanded-candidate",
            "index-expanded-candidate",
            "snapshot-expanded-candidate",
            target,
            [generic, outer],
            [
                Edge("generic-to-target", "snapshot-expanded-candidate", generic.SymbolId, target.SymbolId, null, "Calls"),
                Edge("outer-to-generic", "snapshot-expanded-candidate", outer.SymbolId, generic.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new ExpandedCandidateFixture(
            "build-expanded-candidate",
            "index-expanded-candidate",
            target,
            generic,
            outer);
    }

    private async Task SeedReferenceCollectionAsync(string gameIndexId, string buildId)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-reference-traversal-limit",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "reference-collection",
            "2026-08-29T00:02:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var run = new IndexRunRecord(
            "index-reference-traversal-limit",
            snapshot.SnapshotId,
            IndexRunStatus.Running,
            snapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(run, TestContext.Current.CancellationToken);
        var symbol = Method("reference-unrelated", snapshot.SnapshotId, "qol.Unrelated.Run", BodyRecoveryStatus.Recovered);

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [symbol],
                [],
                [],
                [],
                [],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        "mods/qol",
                        "qol-content",
                        [symbol.SymbolId])
                ]),
            "2026-08-29T00:03:00Z",
            TestContext.Current.CancellationToken);
    }

    private async Task<MethodFixture> SeedDeterministicOrderingFixtureAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedEnvironmentAsync("build-order");

        var target = Method("target-order", "snapshot-order", "Game.Seams.OrderTarget.Run", BodyRecoveryStatus.Recovered);
        var ownerShort = Method("owner-short", "snapshot-order", "Owner.Short.Direct", BodyRecoveryStatus.Recovered);
        var bridgeMixed = Constructor("bridge-mixed", "snapshot-order", "Bridge.Mixed..ctor", BodyRecoveryStatus.Recovered);
        var ownerMixed = Method("owner-mixed", "snapshot-order", "Owner.Mixed.Build", BodyRecoveryStatus.Recovered);
        var bridgeAlpha = Method("bridge-alpha", "snapshot-order", "Bridge.Alpha.Run", BodyRecoveryStatus.Recovered);
        var ownerAlpha = Method("owner-alpha", "snapshot-order", "Alpha.Owner.Run", BodyRecoveryStatus.Recovered);
        var bridgeTie = Method("bridge-tie", "snapshot-order", "Bridge.Tie.Run", BodyRecoveryStatus.Recovered);
        var tieA = new IndexSymbolRecord(
            "tie-a",
            "snapshot-order",
            "ScheduleI:Installed:Method:Tie.Owner::RunA()",
            "Method",
            "Tie.Owner.Run",
            "System.Void Tie.Owner::Run()",
            false,
            BodyRecoveryStatus.Recovered);
        var tieB = new IndexSymbolRecord(
            "tie-b",
            "snapshot-order",
            "ScheduleI:Installed:Method:Tie.Owner::RunB()",
            "Method",
            "Tie.Owner.Run",
            "System.Void Tie.Owner::Run()",
            false,
            BodyRecoveryStatus.Recovered);

        await CompleteGameRunAsync(
            "build-order",
            "index-order",
            "snapshot-order",
            target,
            [ownerShort, bridgeMixed, ownerMixed, bridgeAlpha, ownerAlpha, bridgeTie, tieA, tieB],
            [
                Edge("edge-001-owner-short", "snapshot-order", ownerShort.SymbolId, target.SymbolId, null, "Calls"),
                Edge("edge-010-bridge-mixed", "snapshot-order", bridgeMixed.SymbolId, target.SymbolId, null, "Calls"),
                Edge("edge-011-owner-mixed", "snapshot-order", ownerMixed.SymbolId, bridgeMixed.SymbolId, null, "Constructs"),
                Edge("edge-020-bridge-alpha", "snapshot-order", bridgeAlpha.SymbolId, target.SymbolId, null, "Calls"),
                Edge("edge-021-owner-alpha", "snapshot-order", ownerAlpha.SymbolId, bridgeAlpha.SymbolId, null, "Calls"),
                Edge("edge-030-bridge-tie", "snapshot-order", bridgeTie.SymbolId, target.SymbolId, null, "Calls"),
                Edge("edge-031-owner-tie-a", "snapshot-order", tieA.SymbolId, bridgeTie.SymbolId, null, "Calls"),
                Edge("edge-032-owner-tie-b", "snapshot-order", tieB.SymbolId, bridgeTie.SymbolId, null, "Calls")
            ],
            includeCallableSurface: true);

        return new MethodFixture("build-order", "index-order", target);
    }

    private async Task SeedEnvironmentAsync(string buildId)
    {
        await _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, Sha256("assembly-" + buildId), Sha256("metadata-" + buildId), DateTimeOffset.Parse("2026-08-29T00:00:00Z"), true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.2.0-test",
                DateTimeOffset.Parse("2026-08-29T00:00:00Z")),
            TestContext.Current.CancellationToken);
    }

    private async Task CompleteGameRunAsync(
        string buildId,
        string indexId,
        string snapshotId,
        IndexSymbolRecord target,
        IReadOnlyList<IndexSymbolRecord> additionalSymbols,
        IReadOnlyList<IndexRelationshipRecord> relationships,
        bool includeCallableSurface,
        string completedAtUtc = "2026-08-29T00:01:00Z")
    {
        var environment = await _repository.GetCurrentSnapshotAsync(TestContext.Current.CancellationToken);
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-" + indexId,
            "2026-08-29T00:00:00Z",
            environment is null ? null : EnvironmentSnapshotId.Create(environment));
        await _repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            TestContext.Current.CancellationToken);

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
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
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
                    : []),
            completedAtUtc,
            TestContext.Current.CancellationToken);

        var indexRoot = Path.Combine(_dataRoot, "builds", buildId, "indexes", indexId);
        Directory.CreateDirectory(indexRoot);
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, sourceFile.RelativePath),
            sourceText,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
    }

    private static IndexSymbolRecord Method(
        string id,
        string snapshotId,
        string qualifiedName,
        BodyRecoveryStatus status,
        string? canonicalMember = null)
    {
        var member = canonicalMember ?? CanonicalMember(qualifiedName);
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

    private static IndexSymbolRecord Constructor(
        string id,
        string snapshotId,
        string qualifiedName,
        BodyRecoveryStatus status)
    {
        var member = qualifiedName.Replace("..ctor", "::.ctor", StringComparison.Ordinal);
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ScheduleI:Installed:Constructor:" + member + "()",
            "Constructor",
            qualifiedName,
            "System.Void " + member + "()",
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

    private static SymbolQueryResult Symbol(string symbolId, string qualifiedName, string signature) =>
        new(
            "index-1",
            "ScheduleI",
            "Installed",
            symbolId,
            "Method",
            qualifiedName,
            signature,
            false);

    private sealed record MethodFixture(string BuildId, string IndexId, IndexSymbolRecord Target);
    private sealed record TraversalLimitFixture(
        string BuildId,
        string IndexId,
        IndexSymbolRecord Target,
        IndexSymbolRecord Owner,
        IndexSymbolRecord? SecondOwner);
    private sealed record DepthBoundedTraversalFixture(
        IndexSymbolRecord Target,
        IndexSymbolRecord DeepestReachable,
        IndexSymbolRecord SkippedAtMaxDepth);
    private sealed record FieldDirectionFixture(IndexSymbolRecord Target);
    private sealed record ReferenceFieldFixture(IndexSymbolRecord Target, string Collection);
    private sealed record CrossOriginOwnerFixture(
        IndexSymbolRecord Target,
        IndexSymbolRecord Owner,
        string Collection,
        string ReferenceIndexId);
    private sealed record SupportableTypeFixture(IndexSymbolRecord Target);
    private sealed record BoundedEvidenceFixture(IndexSymbolRecord Target);
    private sealed record ExpandedCandidateFixture(
        string BuildId,
        string IndexId,
        IndexSymbolRecord Target,
        IndexSymbolRecord Generic,
        IndexSymbolRecord Outer);
}
