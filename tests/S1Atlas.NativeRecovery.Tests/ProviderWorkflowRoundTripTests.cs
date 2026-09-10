using AssetRipper.Primitives;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

/// <summary>
/// Task 2.6: proves <see cref="LibCpp2IlNativeBodyRecoveryProvider"/> integrates correctly through
/// the real <see cref="NativeRecoveryWorkflow"/> — not a fake workflow. The provider is wired with
/// its injectable fakes (<see cref="ILibCpp2IlAdapterFactory"/>, <see cref="ISymbolIdentityResolver"/>)
/// so no game is required, but the workflow itself is the production type.
/// </summary>
public class ProviderWorkflowRoundTripTests
{
    private static readonly IReadOnlyList<LibraryPin> Pins =
    [
        new LibraryPin("Samboy063.LibCpp2IL", "2022.1.0-pre-release.21", "test-hash-libcpp2il"),
        new LibraryPin("Iced", "1.21.0", "test-hash-iced"),
    ];

    private const string GameAssemblySha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Assembles `call rel32` (opcode 0xE8 + 4-byte little-endian relative displacement) targeting
    // the given absolute virtual address, given the instruction starts at `at`.
    private static byte[] CallRel32(ulong at, ulong target)
    {
        var nextIp = at + 5; // E8 + 4-byte rel32
        var rel32 = unchecked((int)(target - nextIp));
        var bytes = new byte[5];
        bytes[0] = 0xE8;
        BitConverter.GetBytes(rel32).CopyTo(bytes, 1);
        return bytes;
    }

    private static byte[] Ret() => [0xC3];

    private static byte[] Concat(params byte[][] chunks)
    {
        var result = new List<byte>();
        foreach (var chunk in chunks)
        {
            result.AddRange(chunk);
        }

        return [.. result];
    }

    private static NativeRecoveryRequest CreateRequest(IReadOnlyList<string> symbolIds, int maxTraversalEdges = 10) =>
        new(
            BuildId: "build-1",
            IndexId: "index-1",
            GameAssemblySha256: GameAssemblySha256,
            SymbolIds: symbolIds,
            MaxTraversalEdges: maxTraversalEdges);

    private static NativeRecoveryExecutionContext CreateMatchingExecutionContext(NativeRecoveryRequest request) =>
        new(
            CurrentBuildId: request.BuildId,
            CurrentIndexId: request.IndexId,
            CurrentGameAssemblySha256: request.GameAssemblySha256,
            ToolName: LibraryToolIdentity.ToolName,
            ToolVersion: LibraryToolIdentity.ToolVersion(Pins),
            ToolSha256: LibraryToolIdentity.ComputeToolSha256(Pins));

    private static Il2CppImageCache CreateNoOpImageCache() =>
        new((_, _, _, _, _, _) => Task.CompletedTask);

    private sealed class FakeNativeImageSource(byte[] gameAssemblyBytes) : INativeImageSource
    {
        public Task<NativeImage> GetImageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NativeImage(gameAssemblyBytes, "game-assembly-sha", [0x01], "metadata-sha"));
    }

    private sealed class FakeSymbolIdentityResolver(
        IReadOnlyDictionary<string, ManagedSymbolDescriptor?> descriptorsBySymbolId) : ISymbolIdentityResolver
    {
        public ManagedSymbolDescriptor? Resolve(string symbolId) =>
            descriptorsBySymbolId.TryGetValue(symbolId, out var descriptor) ? descriptor : null;
    }

    private sealed class FakeMethodLookup : IIl2CppMethodLookup
    {
        private readonly Dictionary<(string Type, string Name), List<NativeMethodCandidate>> _candidates = new();

        public void Add(string declaringTypeFullName, string methodName, NativeMethodCandidate candidate)
        {
            var key = (declaringTypeFullName, methodName);
            if (!_candidates.TryGetValue(key, out var list))
            {
                list = [];
                _candidates[key] = list;
            }

            list.Add(candidate);
        }

        public IReadOnlyList<NativeMethodCandidate> FindByTypeAndName(string declaringTypeFullName, string methodName) =>
            _candidates.TryGetValue((declaringTypeFullName, methodName), out var list)
                ? list
                : [];
    }

    private sealed class FixedAddressResolver(AddressResolutionKind kind, string? managedName = null) : IAddressResolver
    {
        public AddressResolution Resolve(ulong virtualAddress) => new(kind, managedName);
    }

    private sealed class NullFieldResolver : IFieldResolver
    {
        public string? ResolveFieldName(ulong offset) => null;
    }

    private sealed class FakeAdapterFactory(IIl2CppMethodLookup lookup, IAddressResolver addressResolver) : ILibCpp2IlAdapterFactory
    {
        public IIl2CppMethodLookup CreateMethodLookup() => lookup;

        public IAddressResolver CreateAddressResolver() => addressResolver;

        public IFieldResolver CreateFieldResolver(string declaringTypeFullName) => new NullFieldResolver();
    }

    private static LibCpp2IlNativeBodyRecoveryProvider CreateProvider(
        byte[] gameAssemblyBytes,
        ISymbolIdentityResolver symbolIdentityResolver,
        ILibCpp2IlAdapterFactory adapterFactory) =>
        new(
            CreateNoOpImageCache(),
            new FakeNativeImageSource(gameAssemblyBytes),
            symbolIdentityResolver,
            adapterFactory,
            UnityVersion.Parse("2022.3.62f2"),
            Pins);

    [Fact]
    public async Task RecoverAsync_ThroughRealWorkflow_RunTwiceWithIdenticalInputs_ProducesIdenticalOutputShaAndRecoveryId()
    {
        const ulong methodVa = 0x1000;
        const ulong targetVa = 0x9000;
        var methodBytes = Concat(CallRel32(methodVa, targetVa), Ret());

        var lookup = new FakeMethodLookup();
        lookup.Add("Foo.Bar", "Method1", new NativeMethodCandidate(
            methodVa, MethodOffsetInFile: 0, Rva: methodVa, "Foo.Bar", "Method1", []));

        var addressResolver = new FixedAddressResolver(AddressResolutionKind.Single, "Some.Target.Method");
        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            ["sym1"] = new ManagedSymbolDescriptor("Foo.Bar", "Method1", []),
        });

        var provider = CreateProvider(methodBytes, symbolIdentityResolver, new FakeAdapterFactory(lookup, addressResolver));
        var workflow = new NativeRecoveryWorkflow(provider);

        var request = CreateRequest(["sym1"]);
        var executionContext = CreateMatchingExecutionContext(request);

        var first = await workflow.RecoverAsync(request, executionContext, CancellationToken.None);
        var second = await workflow.RecoverAsync(request, executionContext, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.Recovered, first.Status);
        Assert.Equal(NativeRecoveryStatus.Recovered, second.Status);
        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.RecoveryId, second.RecoveryId);
        Assert.NotEmpty(first.OutputSha256);
        Assert.NotEmpty(first.RecoveryId);
    }

    [Fact]
    public async Task RecoverAsync_ThroughRealWorkflow_NonDirectCallEdgeFromProvider_IsNormalizedToUnknownAndIncomplete()
    {
        const ulong methodVa = 0x1000;
        const ulong targetVa = 0x9000;
        var methodBytes = Concat(CallRel32(methodVa, targetVa), Ret());

        var lookup = new FakeMethodLookup();
        lookup.Add("Foo.Bar", "Method1", new NativeMethodCandidate(
            methodVa, MethodOffsetInFile: 0, Rva: methodVa, "Foo.Bar", "Method1", []));

        // AddressResolutionKind.None means the call target does not resolve to a known managed
        // method, so BoundedNativeDecoder's ClassifyCall emits a "RuntimeDispatch" edge (not
        // "DirectCall") — the real decoder's representation of an indirect/unresolved call.
        var addressResolver = new FixedAddressResolver(AddressResolutionKind.None);
        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            ["sym1"] = new ManagedSymbolDescriptor("Foo.Bar", "Method1", []),
        });

        var provider = CreateProvider(methodBytes, symbolIdentityResolver, new FakeAdapterFactory(lookup, addressResolver));
        var workflow = new NativeRecoveryWorkflow(provider);

        var request = CreateRequest(["sym1"]);
        var executionContext = CreateMatchingExecutionContext(request);

        var record = await workflow.RecoverAsync(request, executionContext, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.Recovered, record.Status);
        var edge = Assert.Single(record.Edges);
        Assert.Equal("UNKNOWN", edge.Kind);
        Assert.Null(edge.TargetMethodPointer);
        Assert.False(edge.IsComplete);
        Assert.False(record.IsComplete);
    }

    [Fact]
    public async Task RecoverAsync_ThroughRealWorkflow_ProviderEvidenceContainsForbiddenChar_ReturnsFailedInvalidEvidence()
    {
        // The provider embeds the caller-supplied symbol id verbatim into its mapping-evidence
        // string (DescribeResolution: "{symbolId} resolved to ..."). A symbol id containing '/'
        // therefore flows an unsafe character into evidence text that the provider itself never
        // sanitizes — proving that NativeRecoveryWorkflow.SanitizeSummary is a real backstop for
        // the provider's own NativeNameNormalizer.IsSummarySafe guarantee, not just a redundant
        // check exercised via a hand-rolled fake provider.
        const string unsafeSymbolId = "sym/1";
        const ulong methodVa = 0x1000;

        var lookup = new FakeMethodLookup();
        lookup.Add("Foo.Bar", "Method1", new NativeMethodCandidate(
            methodVa, MethodOffsetInFile: 0, Rva: methodVa, "Foo.Bar", "Method1", []));

        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            [unsafeSymbolId] = new ManagedSymbolDescriptor("Foo.Bar", "Method1", []),
        });

        var provider = CreateProvider(
            Concat(CallRel32(methodVa, 0x9000), Ret()),
            symbolIdentityResolver,
            new FakeAdapterFactory(lookup, new FixedAddressResolver(AddressResolutionKind.Single, "Some.Target.Method")));
        var workflow = new NativeRecoveryWorkflow(provider);

        var request = CreateRequest([unsafeSymbolId]);
        var executionContext = CreateMatchingExecutionContext(request);

        var record = await workflow.RecoverAsync(request, executionContext, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.Failed, record.Status);
        Assert.Equal("Native recovery provider returned invalid evidence.", record.FailureMessage);
        Assert.Empty(record.Edges);
        Assert.Empty(record.MappingEvidence);
        Assert.False(record.IsComplete);
    }
}
