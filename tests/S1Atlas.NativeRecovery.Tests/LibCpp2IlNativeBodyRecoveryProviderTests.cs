using AssetRipper.Primitives;
using S1Atlas.Core.Storage;
using S1Atlas.NativeRecovery;
using S1Atlas.NativeRecovery.Tests.Spikes;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class LibCpp2IlNativeBodyRecoveryProviderTests
{
    private static readonly IReadOnlyList<LibraryPin> Pins =
    [
        new LibraryPin("Samboy063.LibCpp2IL", "2022.1.0-pre-release.21", "test-hash-libcpp2il"),
        new LibraryPin("Iced", "1.21.0", "test-hash-iced"),
    ];

    // Assembles `call rel32` (opcode 0xE8 + 4-byte little-endian relative displacement)
    // targeting the given absolute virtual address, given the instruction starts at `at`.
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
            GameAssemblySha256: new string('a', 64),
            SymbolIds: symbolIds,
            MaxTraversalEdges: maxTraversalEdges);

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

    private sealed class ThrowingSymbolIdentityResolver : ISymbolIdentityResolver
    {
        public ManagedSymbolDescriptor? Resolve(string symbolId) =>
            throw new InvalidOperationException("Symbol identity lookup failed.");
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

    [Fact]
    public async Task RecoverAsync_AllSymbolsResolved_ReturnsRecoveredWithAggregatedEdgesAndMatchingRequest()
    {
        const ulong method1Va = 0x1000;
        const ulong method2Va = 0x2000;
        const ulong targetVa = 0x9000;

        var method1Bytes = Concat(CallRel32(method1Va, targetVa), Ret());
        var method2Bytes = Concat(CallRel32(method2Va, targetVa), Ret());
        var gameAssemblyBytes = Concat(method1Bytes, method2Bytes);

        var lookup = new FakeMethodLookup();
        lookup.Add("Foo.Bar", "Method1", new NativeMethodCandidate(
            method1Va, MethodOffsetInFile: 0, Rva: method1Va, "Foo.Bar", "Method1", []));
        lookup.Add("Foo.Bar", "Method2", new NativeMethodCandidate(
            method2Va, MethodOffsetInFile: method1Bytes.Length, Rva: method2Va, "Foo.Bar", "Method2", []));

        var addressResolver = new FixedAddressResolver(AddressResolutionKind.Single, "Some.Target.Method");
        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            ["sym1"] = new ManagedSymbolDescriptor("Foo.Bar", "Method1", []),
            ["sym2"] = new ManagedSymbolDescriptor("Foo.Bar", "Method2", []),
        });

        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            CreateNoOpImageCache(),
            new FakeNativeImageSource(gameAssemblyBytes),
            symbolIdentityResolver,
            new FakeAdapterFactory(lookup, addressResolver),
            UnityVersion.Parse("2022.3.62f2"),
            Pins);

        var request = CreateRequest(["sym1", "sym2"], maxTraversalEdges: 10);

        var record = await provider.RecoverAsync(request, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.Recovered, record.Status);
        Assert.Equal(request, record.Request);
        Assert.Equal(2, record.Edges.Count);
        Assert.All(record.Edges, edge => Assert.Equal("DirectCall", edge.Kind));
        Assert.All(record.Edges, edge => Assert.True(edge.IsComplete));
        Assert.Equal(2, record.MappingEvidence.Count);
        Assert.True(record.IsComplete);
        Assert.Null(record.FailureMessage);
        Assert.Equal(LibraryToolIdentity.ToolName, record.ToolName);
        Assert.Equal(LibraryToolIdentity.ToolVersion(Pins), record.ToolVersion);
        Assert.Equal(LibraryToolIdentity.ComputeToolSha256(Pins), record.ToolSha256);

        AssertAllEvidenceIsSummarySafe(record);
    }

    [Fact]
    public async Task RecoverAsync_OneSymbolAmbiguous_ReturnsAmbiguousMappingWithNoEdges()
    {
        var lookup = new FakeMethodLookup();
        lookup.Add("Foo.Bar", "Duplicate", new NativeMethodCandidate(0x1000, 0, 0x1000, "Foo.Bar", "Duplicate", []));
        lookup.Add("Foo.Bar", "Duplicate", new NativeMethodCandidate(0x2000, 100, 0x2000, "Foo.Bar", "Duplicate", []));

        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            ["sym1"] = new ManagedSymbolDescriptor("Foo.Bar", "Duplicate", []),
        });

        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            CreateNoOpImageCache(),
            new FakeNativeImageSource(new byte[1024]),
            symbolIdentityResolver,
            new FakeAdapterFactory(lookup, new FixedAddressResolver(AddressResolutionKind.None)),
            UnityVersion.Parse("2022.3.62f2"),
            Pins);

        var request = CreateRequest(["sym1"]);

        var record = await provider.RecoverAsync(request, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.AmbiguousMapping, record.Status);
        Assert.Equal(request, record.Request);
        Assert.Empty(record.Edges);
        Assert.Empty(record.FieldAccesses);
        Assert.NotEmpty(record.MappingEvidence);
        Assert.False(record.IsComplete);

        AssertAllEvidenceIsSummarySafe(record);
    }

    [Fact]
    public async Task RecoverAsync_AllSymbolsNotFound_ReturnsNoBody()
    {
        var lookup = new FakeMethodLookup(); // empty: nothing resolves

        var symbolIdentityResolver = new FakeSymbolIdentityResolver(new Dictionary<string, ManagedSymbolDescriptor?>
        {
            ["sym1"] = new ManagedSymbolDescriptor("Foo.Bar", "Missing", []),
            ["sym2"] = null, // unknown to the index entirely
        });

        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            CreateNoOpImageCache(),
            new FakeNativeImageSource(new byte[1024]),
            symbolIdentityResolver,
            new FakeAdapterFactory(lookup, new FixedAddressResolver(AddressResolutionKind.None)),
            UnityVersion.Parse("2022.3.62f2"),
            Pins);

        var request = CreateRequest(["sym1", "sym2"]);

        var record = await provider.RecoverAsync(request, CancellationToken.None);

        Assert.Equal(NativeRecoveryStatus.NoBody, record.Status);
        Assert.Equal(request, record.Request);
        Assert.Empty(record.Edges);
        Assert.Empty(record.FieldAccesses);
        Assert.NotEmpty(record.MappingEvidence);
        Assert.False(record.IsComplete);

        AssertAllEvidenceIsSummarySafe(record);
    }

    [Fact]
    public async Task RecoverAsync_SymbolIdentityResolverThrows_ExceptionPropagates()
    {
        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            CreateNoOpImageCache(),
            new FakeNativeImageSource(new byte[1024]),
            new ThrowingSymbolIdentityResolver(),
            new FakeAdapterFactory(new FakeMethodLookup(), new FixedAddressResolver(AddressResolutionKind.None)),
            UnityVersion.Parse("2022.3.62f2"),
            Pins);

        var request = CreateRequest(["sym1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RecoverAsync(request, CancellationToken.None));
    }

    private static void AssertAllEvidenceIsSummarySafe(NativeRecoveryRecord record)
    {
        foreach (var evidence in record.MappingEvidence)
        {
            Assert.True(NativeNameNormalizer.IsSummarySafe(evidence), $"Mapping evidence '{evidence}' is not summary-safe.");
        }

        foreach (var edge in record.Edges)
        {
            Assert.True(NativeNameNormalizer.IsSummarySafe(edge.SourceMethodPointer));
            if (edge.TargetMethodPointer is not null)
                Assert.True(NativeNameNormalizer.IsSummarySafe(edge.TargetMethodPointer));
            if (edge.TargetText is not null)
                Assert.True(NativeNameNormalizer.IsSummarySafe(edge.TargetText));
            Assert.True(NativeNameNormalizer.IsSummarySafe(edge.Kind));
            Assert.True(NativeNameNormalizer.IsSummarySafe(edge.Evidence));
        }

        foreach (var fieldAccess in record.FieldAccesses)
        {
            Assert.True(NativeNameNormalizer.IsSummarySafe(fieldAccess));
        }
    }

    // ---------------------------------------------------------------------------------------
    // LocalGameRequired: exercises the REAL LibCpp2IL-backed adapters end-to-end against the
    // installed Schedule I build. Skips (does not fail) when the game is not installed, mirroring
    // the Task 1.1/1.2 spikes so CI without the game stays green.
    // ---------------------------------------------------------------------------------------
    [Collection(LibCpp2IlGlobalStateCollection.Name)]
    [Trait("Category", "LocalGameRequired")]
    public sealed class RealAdapterEndToEndTests
    {
        private sealed class RealNativeImageSource : INativeImageSource
        {
            public Task<NativeImage> GetImageAsync(CancellationToken cancellationToken)
            {
                var (binaryBytes, metadataBytes) = LocalGameFixture.ReadImageBytes();
                var gameAssemblySha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(binaryBytes)).ToLowerInvariant();
                var metadataSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(metadataBytes)).ToLowerInvariant();
                return Task.FromResult(new NativeImage(binaryBytes, gameAssemblySha256, metadataBytes, metadataSha256));
            }
        }

        private sealed class FixedSymbolIdentityResolver(ManagedSymbolDescriptor descriptor) : ISymbolIdentityResolver
        {
            public ManagedSymbolDescriptor? Resolve(string symbolId) => descriptor;
        }

        [Fact]
        public async Task RecoverAsync_CustomerEvaluateCounteroffer_RecoversAgainstRealBuild()
        {
            LocalGameFixture.SkipUnlessAvailable();

            var imageCache = new Il2CppImageCache(async (_, gameAssemblyBytes, _, metadataBytes, unityVersion, _) =>
            {
                LibCpp2IL.LibCpp2IlMain.Reset();
                var initialized = LibCpp2IL.LibCpp2IlMain.Initialize(gameAssemblyBytes, metadataBytes, unityVersion);
                if (!initialized)
                    throw new InvalidOperationException("LibCpp2IlMain.Initialize failed against the installed build.");
                await Task.CompletedTask;
            });

            var descriptor = new ManagedSymbolDescriptor(
                $"{CustomerLookup.DeclaringNamespace}.{CustomerLookup.TypeName}",
                "EvaluateCounteroffer",
                ["ScheduleOne.Product.ProductDefinition", "System.Int32", "System.Single"]);

            var provider = new LibCpp2IlNativeBodyRecoveryProvider(
                imageCache,
                new RealNativeImageSource(),
                new FixedSymbolIdentityResolver(descriptor),
                new LibCpp2IlAdapterFactory(),
                AssetRipper.Primitives.UnityVersion.Parse(CustomerLookup.UnitySupportedVersion),
                LibCpp2IlNativeBodyRecoveryProvider.LoadLibraryPinsFromConfig(FindLibrariesConfigPath()));

            var request = new NativeRecoveryRequest(
                BuildId: "local-build",
                IndexId: "local-index",
                GameAssemblySha256: new string('a', 64),
                SymbolIds: ["customer-evaluate-counteroffer"],
                MaxTraversalEdges: 50);

            var record = await provider.RecoverAsync(request, CancellationToken.None);

            Assert.Equal(NativeRecoveryStatus.Recovered, record.Status);
            Assert.Equal(request, record.Request);
            Assert.NotEmpty(record.MappingEvidence);
            Assert.NotEmpty(record.Edges);

            AssertAllEvidenceIsSummarySafe(record);
        }

        private static string FindLibrariesConfigPath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "config", "native-recovery", "libraries.json");
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate config/native-recovery/libraries.json.");
        }
    }
}
