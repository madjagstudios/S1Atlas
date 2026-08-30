using S1Atlas.Indexing.NativeRecovery;
using S1Atlas.Core.Storage;
using Xunit;

namespace S1Atlas.Indexing.Tests.NativeRecovery;

public sealed class NativeRecoveryWorkflowTests
{
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-08-29T12:34:56Z");

    [Fact]
    public async Task RecoverAsync_bounds_and_canonicalizes_recovered_evidence()
    {
        var request = Request(maxTraversalEdges: 2);
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: ["wrapper:0x20->native:0x200", "wrapper:0x10->native:0x100"],
            edges:
            [
                Edge("edge-c", "0x300", "0x301"),
                Edge("edge-a", "0x100", "0x101"),
                Edge("edge-b", "0x200", "0x201")
            ],
            fieldAccesses: ["0x200 writes field-z", "0x100 reads field-a"],
            isComplete: true));
        var workflow = Workflow(provider);

        var result = await workflow.RecoverAsync(
            request,
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.Recovered, result.Status);
        Assert.Equal(request.BuildId, result.Request.BuildId);
        Assert.Equal(request.IndexId, result.Request.IndexId);
        Assert.Equal(request.GameAssemblySha256, result.Request.GameAssemblySha256);
        Assert.Equal(["symbol-a", "symbol-b"], result.Request.SymbolIds);
        Assert.Equal(["wrapper:0x10->native:0x100", "wrapper:0x20->native:0x200"], result.MappingEvidence);
        Assert.Equal(2, result.Edges.Count);
        Assert.All(result.Edges, edge => Assert.Matches("^[0-9a-f]{64}$", edge.EdgeId));
        Assert.Equal(["0x100", "0x200"], result.Edges.Select(edge => edge.SourceMethodPointer));
        Assert.Equal(["0x101", "0x201"], result.Edges.Select(edge => edge.TargetMethodPointer));
        Assert.All(result.Edges, edge => Assert.Equal("DirectCall", edge.Kind));
        Assert.Equal(["0x100 reads field-a", "0x200 writes field-z"], result.FieldAccesses);
        Assert.False(result.IsComplete);
        Assert.Equal("SyntheticNativeTool", result.ToolName);
        Assert.Equal("1.2.3", result.ToolVersion);
        Assert.Equal(Hash('d'), result.ToolSha256);
        Assert.Equal(RecordedAt, result.CreatedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", result.OutputSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.RecoveryId);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task RecoverAsync_preserves_no_body_without_native_edges_or_fields()
    {
        var request = Request();
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.NoBody,
            mappingEvidence: ["symbol-a maps to 0x100 but no body was emitted"],
            edges: [Edge("must-not-survive", "0x100", "0x101")],
            fieldAccesses: ["must-not-survive"],
            isComplete: true,
            failureMessage: "The selected native method has no recoverable body."));

        var result = await Workflow(provider).RecoverAsync(
            request,
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.NoBody, result.Status);
        Assert.Equal(["symbol-a maps to 0x100 but no body was emitted"], result.MappingEvidence);
        Assert.Empty(result.Edges);
        Assert.Empty(result.FieldAccesses);
        Assert.False(result.IsComplete);
        Assert.Equal("The selected native method has no recoverable body.", result.FailureMessage);
    }

    [Fact]
    public async Task RecoverAsync_preserves_ambiguous_mapping_as_an_explicit_result()
    {
        var request = Request();
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.AmbiguousMapping,
            mappingEvidence: ["symbol-a -> 0x100", "symbol-a -> 0x200"],
            failureMessage: "Multiple native method pointers matched symbol-a."));

        var result = await Workflow(provider).RecoverAsync(
            request,
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.AmbiguousMapping, result.Status);
        Assert.Equal(["symbol-a -> 0x100", "symbol-a -> 0x200"], result.MappingEvidence);
        Assert.Empty(result.Edges);
        Assert.Empty(result.FieldAccesses);
        Assert.False(result.IsComplete);
        Assert.Equal("Multiple native method pointers matched symbol-a.", result.FailureMessage);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("index")]
    [InlineData("game-assembly")]
    public async Task RecoverAsync_returns_input_changed_before_invoking_provider(string changedIdentity)
    {
        var provider = new FakeProvider(_ => throw new InvalidOperationException("Provider must not run for changed inputs."));
        var context = changedIdentity switch
        {
            "build" => MatchingContext() with { CurrentBuildId = "build-2" },
            "index" => MatchingContext() with { CurrentIndexId = "index-2" },
            "game-assembly" => MatchingContext() with { CurrentGameAssemblySha256 = Hash('e') },
            _ => throw new ArgumentOutOfRangeException(nameof(changedIdentity))
        };

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.InputChanged, result.Status);
        Assert.Empty(result.MappingEvidence);
        Assert.Empty(result.Edges);
        Assert.Empty(result.FieldAccesses);
        Assert.False(result.IsComplete);
        Assert.Contains("does not match", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(Hash('d'), result.ToolSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.OutputSha256);
    }

    [Fact]
    public async Task RecoverAsync_captures_provider_failure_with_tool_provenance()
    {
        var provider = new ThrowingProvider(new InvalidOperationException("synthetic provider failure"));

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.Failed, result.Status);
        Assert.Equal("SyntheticNativeTool", result.ToolName);
        Assert.Equal("1.2.3", result.ToolVersion);
        Assert.Equal(Hash('d'), result.ToolSha256);
        Assert.Empty(result.MappingEvidence);
        Assert.Empty(result.Edges);
        Assert.Empty(result.FieldAccesses);
        Assert.False(result.IsComplete);
        Assert.Equal("Native recovery provider failed.", result.FailureMessage);
        Assert.Matches("^[0-9a-f]{64}$", result.OutputSha256);
        Assert.Equal(RecordedAt, result.CreatedAtUtc);
    }

    [Fact]
    public async Task RecoverAsync_returns_unsupported_when_no_provider_is_configured()
    {
        var result = await Workflow(provider: null).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.Unsupported, result.Status);
        Assert.Equal("No native body recovery provider is configured.", result.FailureMessage);
        Assert.Equal("SyntheticNativeTool", result.ToolName);
        Assert.Equal("1.2.3", result.ToolVersion);
        Assert.Equal(Hash('d'), result.ToolSha256);
        Assert.Empty(result.MappingEvidence);
        Assert.Empty(result.Edges);
        Assert.Empty(result.FieldAccesses);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task RecoverAsync_rejects_traversal_budgets_outside_the_contract(int maxTraversalEdges)
    {
        var workflow = Workflow(new FakeProvider(ProviderRecord));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workflow.RecoverAsync(
            Request(maxTraversalEdges),
            MatchingContext(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoverAsync_rejects_blank_build_or_index_identity()
    {
        var workflow = Workflow(new FakeProvider(ProviderRecord));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { BuildId = " " },
            MatchingContext(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { IndexId = "" },
            MatchingContext(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoverAsync_rejects_invalid_sha256_values()
    {
        var workflow = Workflow(new FakeProvider(ProviderRecord));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { GameAssemblySha256 = new string('a', 63) },
            MatchingContext(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request(),
            MatchingContext() with { ToolSha256 = new string('D', 64) },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoverAsync_rejects_empty_or_invalid_symbol_selections()
    {
        var workflow = Workflow(new FakeProvider(ProviderRecord));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { SymbolIds = [] },
            MatchingContext(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { SymbolIds = ["symbol-a", " "] },
            MatchingContext(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workflow.RecoverAsync(
            Request() with { SymbolIds = ["symbol-a", "symbol-a"] },
            MatchingContext(),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("IndirectDispatch")]
    [InlineData("RuntimeDispatch")]
    [InlineData("CrossThreadDispatch")]
    public async Task RecoverAsync_classifies_non_direct_dispatch_as_unknown(string kind)
    {
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: ["symbol-a -> 0x100"],
            edges:
            [
                new NativeEvidenceEdge(
                    "edge-a",
                    "0x100",
                    TargetMethodPointer: null,
                    TargetText: "target selected outside direct static evidence",
                    kind,
                    "target inferred from dispatch shape",
                    IsComplete: true)
            ],
            isComplete: true));

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        var edge = Assert.Single(result.Edges);
        Assert.Equal("UNKNOWN", edge.Kind);
        Assert.Equal("UNKNOWN: target inferred from dispatch shape", edge.Evidence);
        Assert.Null(edge.TargetMethodPointer);
        Assert.False(edge.IsComplete);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task RecoverAsync_classifies_unrecognized_and_targetless_edges_as_unknown()
    {
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: ["symbol-a -> 0x100"],
            edges:
            [
                new NativeEvidenceEdge("provider-id", "0x100", "0x101", null, "OtherDispatch", "unrecognized dispatch", true),
                new NativeEvidenceEdge("provider-id-2", "0x100", null, "target text", "DirectCall", "missing target", true)
            ],
            isComplete: true));

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.All(result.Edges, edge =>
        {
            Assert.Equal("UNKNOWN", edge.Kind);
            Assert.Null(edge.TargetMethodPointer);
            Assert.False(edge.IsComplete);
        });
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task RecoverAsync_rejects_complete_recovery_without_evidence()
    {
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: [],
            edges: [],
            fieldAccesses: [],
            isComplete: true));

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.Failed, result.Status);
        Assert.False(result.IsComplete);
        Assert.Empty(result.MappingEvidence);
        Assert.Equal("Native recovery provider returned invalid evidence.", result.FailureMessage);
    }

    [Fact]
    public async Task RecoverAsync_rejects_artifact_like_provider_text_without_copying_it()
    {
        var provider = new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: ["C:\\secret\\native-dump.bin"],
            edges: [Edge("provider-id", "0x100", "0x101")],
            isComplete: true));

        var result = await Workflow(provider).RecoverAsync(
            Request(),
            MatchingContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeRecoveryStatus.Failed, result.Status);
        Assert.DoesNotContain("secret", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Native recovery provider returned invalid evidence.", result.FailureMessage);
        Assert.Empty(result.MappingEvidence);
    }

    [Fact]
    public async Task RecoverAsync_derives_edge_ids_from_evidence_not_provider_ids()
    {
        var first = await Workflow(new FakeProvider(providerRequest => ProviderRecord(
                providerRequest,
                NativeRecoveryStatus.Recovered,
                mappingEvidence: ["symbol-a -> 0x100"],
                edges: [Edge("provider-id-a", "0x100", "0x101")],
                fieldAccesses: ["0x100 reads field-a"],
                isComplete: true)))
            .RecoverAsync(Request(), MatchingContext(), TestContext.Current.CancellationToken);
        var second = await Workflow(new FakeProvider(providerRequest => ProviderRecord(
                providerRequest,
                NativeRecoveryStatus.Recovered,
                mappingEvidence: ["symbol-a -> 0x100"],
                edges: [Edge("provider-id-b", "0x100", "0x101")],
                fieldAccesses: ["0x100 reads field-a"],
                isComplete: true)))
            .RecoverAsync(Request(), MatchingContext(), TestContext.Current.CancellationToken);

        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.RecoveryId, second.RecoveryId);
        Assert.Equal(first.Edges.Single().EdgeId, second.Edges.Single().EdgeId);
    }

    [Fact]
    public async Task RecoverAsync_uses_total_edge_order_at_the_truncation_boundary()
    {
        var completeEdge = Edge("provider-complete", "0x100", "0x101");
        var incompleteEdge = completeEdge with { EdgeId = "provider-incomplete", IsComplete = false };
        var reverse = false;
        var provider = new FakeProvider(providerRequest =>
        {
            reverse = !reverse;
            return ProviderRecord(
                providerRequest,
                NativeRecoveryStatus.Recovered,
                mappingEvidence: ["symbol-a -> 0x100"],
                edges: reverse ? [completeEdge, incompleteEdge] : [incompleteEdge, completeEdge],
                fieldAccesses: ["0x100 reads field-a"],
                isComplete: true);
        });
        var workflow = Workflow(provider);

        var first = await workflow.RecoverAsync(Request(maxTraversalEdges: 1), MatchingContext(), TestContext.Current.CancellationToken);
        var second = await workflow.RecoverAsync(Request(maxTraversalEdges: 1), MatchingContext(), TestContext.Current.CancellationToken);

        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.RecoveryId, second.RecoveryId);
        Assert.False(first.IsComplete);
        Assert.False(first.Edges.Single().IsComplete);
        Assert.Equal(first.Edges.Single().EdgeId, second.Edges.Single().EdgeId);
    }

    [Fact]
    public async Task RecoverAsync_derives_identical_record_and_output_hashes_from_equivalent_outputs()
    {
        var reverse = false;
        var provider = new FakeProvider(providerRequest =>
        {
            reverse = !reverse;
            var mappings = reverse
                ? new[] { "map-b", "map-a" }
                : new[] { "map-a", "map-b" };
            var edges = reverse
                ? new[] { Edge("edge-b", "0x200", "0x201"), Edge("edge-a", "0x100", "0x101") }
                : new[] { Edge("edge-a", "0x100", "0x101"), Edge("edge-b", "0x200", "0x201") };
            var fields = reverse
                ? new[] { "field-b", "field-a" }
                : new[] { "field-a", "field-b" };
            return ProviderRecord(
                providerRequest,
                NativeRecoveryStatus.Recovered,
                mappingEvidence: mappings,
                edges: edges,
                fieldAccesses: fields,
                isComplete: true) with
            {
                RecoveryId = Guid.NewGuid().ToString("N"),
                OutputSha256 = Hash(reverse ? 'e' : 'f'),
                CreatedAtUtc = RecordedAt.AddDays(reverse ? 1 : 2)
            };
        });
        var workflow = Workflow(provider);

        var first = await workflow.RecoverAsync(Request(), MatchingContext(), TestContext.Current.CancellationToken);
        var second = await workflow.RecoverAsync(Request(), MatchingContext(), TestContext.Current.CancellationToken);
        var changed = await Workflow(new FakeProvider(providerRequest => ProviderRecord(
            providerRequest,
            NativeRecoveryStatus.Recovered,
            mappingEvidence: ["map-a", "map-c"],
            edges: [Edge("edge-a", "0x100", "0x101"), Edge("edge-b", "0x200", "0x201")],
            fieldAccesses: ["field-a", "field-b"],
            isComplete: true))).RecoverAsync(
                Request(),
                MatchingContext(),
                TestContext.Current.CancellationToken);

        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.RecoveryId, second.RecoveryId);
        Assert.Equal(first.MappingEvidence, second.MappingEvidence);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.FieldAccesses, second.FieldAccesses);
        Assert.Equal("2247c61a00d53dad500fd0930346a66c3236f90915818e41b694e4b181684206", first.OutputSha256);
        Assert.True(first.IsComplete);
        Assert.True(second.IsComplete);
        Assert.NotEqual(first.OutputSha256, changed.OutputSha256);
        Assert.NotEqual(first.RecoveryId, changed.RecoveryId);
        Assert.Equal(RecordedAt, first.CreatedAtUtc);
        Assert.Equal(RecordedAt, second.CreatedAtUtc);
    }

    private static NativeRecoveryWorkflow Workflow(INativeBodyRecoveryProvider? provider) =>
        new(provider, new FixedTimeProvider(RecordedAt));

    private static NativeRecoveryRequest Request(int maxTraversalEdges = 5) =>
        new(
            "build-1",
            "index-1",
            Hash('a'),
            ["symbol-b", "symbol-a"],
            maxTraversalEdges);

    private static NativeRecoveryExecutionContext MatchingContext() =>
        new(
            "build-1",
            "index-1",
            Hash('a'),
            "SyntheticNativeTool",
            "1.2.3",
            Hash('d'));

    private static NativeRecoveryRecord ProviderRecord(NativeRecoveryRequest request) =>
        ProviderRecord(
            request,
            NativeRecoveryStatus.Recovered,
            ["symbol-a -> 0x100"],
            edges: [Edge("provider-id", "0x100", "0x101")],
            isComplete: true);

    private static NativeRecoveryRecord ProviderRecord(
        NativeRecoveryRequest request,
        NativeRecoveryStatus status,
        IReadOnlyList<string>? mappingEvidence = null,
        IReadOnlyList<NativeEvidenceEdge>? edges = null,
        IReadOnlyList<string>? fieldAccesses = null,
        bool isComplete = false,
        string? failureMessage = null) =>
        new(
            "provider-record-id",
            request,
            "SyntheticNativeTool",
            "1.2.3",
            Hash('d'),
            status,
            mappingEvidence ?? [],
            edges ?? [],
            fieldAccesses ?? [],
            isComplete,
            Hash('f'),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"),
            failureMessage);

    private static NativeEvidenceEdge Edge(string edgeId, string source, string target) =>
        new(edgeId, source, target, TargetText: null, "DirectCall", "direct native call", IsComplete: true);

    private static string Hash(char value) => new(value, 64);

    private sealed class FakeProvider(Func<NativeRecoveryRequest, NativeRecoveryRecord> recover) : INativeBodyRecoveryProvider
    {
        public Task<NativeRecoveryRecord> RecoverAsync(
            NativeRecoveryRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(recover(request));
    }

    private sealed class ThrowingProvider(Exception exception) : INativeBodyRecoveryProvider
    {
        public Task<NativeRecoveryRecord> RecoverAsync(
            NativeRecoveryRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<NativeRecoveryRecord>(exception);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
