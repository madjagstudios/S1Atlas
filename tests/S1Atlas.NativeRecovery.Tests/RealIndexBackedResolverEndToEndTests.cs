using AssetRipper.Primitives;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.NativeRecovery;
using S1Atlas.NativeRecovery.Tests.Spikes;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

/// <summary>
/// Task 3.1 acceptance gate: drives <see cref="LibCpp2IlNativeBodyRecoveryProvider"/> through the
/// REAL, index-repository-backed <see cref="IndexSymbolIdentityResolver"/> (not a fake) against
/// the real, locally-installed Schedule I build and a real completed index found in the local
/// S1Atlas database. This proves the parsed index param-type strings (via
/// <see cref="Core.Indexing.CanonicalSignatureParser"/>) actually match LibCpp2IL's own candidate
/// param-type strings for overload disambiguation -- the format-matching risk called out in the
/// Task 3.1 brief.
///
/// Recovery is driven through the real <see cref="NativeRecoveryWorkflow"/> -- not the provider
/// directly -- because the workflow applies canonicalization, evidence sanitization, and the
/// duplicate-edge-id guard that the provider's own output must survive. An earlier version of this
/// gate called the provider directly and so never caught the AT-37 regression where
/// <c>Customer.EvaluateCounteroffer</c>'s 14 unresolved-target call sites collapsed to
/// byte-identical edge tuples: the provider alone returned <c>Recovered</c>, but the same output
/// was rejected as <c>Failed</c> ("duplicate native edge ID") once it passed through the workflow.
///
/// Skips (does not fail) when the game is not installed or no real completed index/current build
/// exists locally to satisfy the persistence/authority preconditions, mirroring every other
/// LocalGameRequired spike so CI without the game and its index stays green.
/// </summary>
[Collection(LibCpp2IlGlobalStateCollection.Name)]
[Trait("Category", "LocalGameRequired")]
public sealed class RealIndexBackedResolverEndToEndTests(ITestOutputHelper output)
{
    private const string DeclaringTypeFullName = "ScheduleOne.Economy.Customer";
    private const string MethodName = "EvaluateCounteroffer";
    private const string QualifiedNamePrefix = $"{DeclaringTypeFullName}::{MethodName}(";

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

    private static string ResolveAtlasHomeDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("S1ATLAS_HOME");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "S1Atlas");
    }

    private static string FindLibrariesConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "native-recovery", "libraries.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate config/native-recovery/libraries.json.");
    }

    [Fact]
    public async Task RecoverAsync_CustomerEvaluateCounteroffer_ThroughRealIndexSymbolIdentityResolver_RecoversWithEdges()
    {
        LocalGameFixture.SkipUnlessAvailable();

        var atlasHomeDirectory = ResolveAtlasHomeDirectory();
        var databasePath = Path.Combine(atlasHomeDirectory, "atlas.db");
        Assert.SkipUnless(
            File.Exists(databasePath),
            $"No S1Atlas database found at '{databasePath}' (override with S1ATLAS_HOME). " +
            "Skipping the Task 3.1 real-resolver acceptance gate: NEEDS_CONTEXT.");

        var repository = new SqliteAtlasRepository(databasePath);
        var cancellationToken = CancellationToken.None;
        await repository.InitializeAsync(cancellationToken);

        // The latest completed Schedule I / Installed index overall -- not correlated through the
        // current environment snapshot's build-id linkage, which is a separate, best-effort join
        // (see GetLatestCompletedIndexForBuildAsync/GetCompletedIndexBuildIdAsync) that need not be
        // populated for every completed index. Any real completed index is sufficient to prove the
        // format-matching gate this test exists for.
        var run = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, environmentSnapshotId: null, cancellationToken);
        Assert.SkipUnless(
            run is not null,
            "No completed Schedule I Installed index exists locally. Skipping: NEEDS_CONTEXT.");

        var buildId = await repository.GetCompletedIndexBuildIdAsync(run!.IndexId, cancellationToken) ?? "local-build";

        var candidates = await repository.SearchCompletedSymbolsAsync(
            run!.IndexId, MethodName, limit: 50, cancellationToken, kind: "Method");
        var symbolRecord = candidates.FirstOrDefault(
            candidate => candidate.QualifiedName.StartsWith(QualifiedNamePrefix, StringComparison.Ordinal));
        Assert.SkipUnless(
            symbolRecord is not null,
            $"No indexed '{DeclaringTypeFullName}::{MethodName}' symbol was found in index '{run.IndexId}'. " +
            "Skipping: NEEDS_CONTEXT.");

        output.WriteLine($"Using indexId='{run.IndexId}', symbolId='{symbolRecord!.SymbolId}'.");
        output.WriteLine($"Indexed signature: {symbolRecord.Signature}");

        var symbolResolver = new IndexSymbolIdentityResolver(repository);
        var resolved = await symbolResolver.ResolveAsync(run.IndexId, [symbolRecord.SymbolId], cancellationToken);
        var descriptor = resolved[symbolRecord.SymbolId];

        Assert.NotNull(descriptor);
        output.WriteLine(
            $"Parsed descriptor: {descriptor!.DeclaringTypeFullName}::{descriptor.MethodName}" +
            $"({string.Join(",", descriptor.ParameterTypeFullNames)})");

        var imageCache = new Il2CppImageCache(async (_, gameAssemblyBytes, _, metadataBytes, unityVersion, _) =>
        {
            LibCpp2IL.LibCpp2IlMain.Reset();
            var initialized = LibCpp2IL.LibCpp2IlMain.Initialize(gameAssemblyBytes, metadataBytes, unityVersion);
            if (!initialized)
            {
                throw new InvalidOperationException("LibCpp2IlMain.Initialize failed against the installed build.");
            }

            await Task.CompletedTask;
        });

        var libraryPins = LibCpp2IlNativeBodyRecoveryProvider.LoadLibraryPinsFromConfig(FindLibrariesConfigPath());
        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            imageCache,
            new RealNativeImageSource(),
            symbolResolver,
            new LibCpp2IlAdapterFactory(),
            UnityVersion.Parse(CustomerLookup.UnitySupportedVersion),
            libraryPins);

        var request = new NativeRecoveryRequest(
            BuildId: buildId,
            IndexId: run.IndexId,
            GameAssemblySha256: new string('a', 64),
            SymbolIds: [symbolRecord.SymbolId],
            MaxTraversalEdges: 50);

        // Task 2.4: drive recovery through the real NativeRecoveryWorkflow -- not the provider
        // directly -- so this gate exercises the canonicalization, evidence sanitization, and
        // duplicate-edge-id guard the provider's output must actually survive. Calling the provider
        // alone (the prior version of this test) missed the AT-37 regression where distinct call
        // sites with identical evidence tuples collapsed to one canonical EdgeId and made the
        // WORKFLOW reject an otherwise-valid Recovered provider result as Failed.
        var executionContext = new NativeRecoveryExecutionContext(
            CurrentBuildId: request.BuildId,
            CurrentIndexId: request.IndexId,
            CurrentGameAssemblySha256: request.GameAssemblySha256,
            ToolName: LibraryToolIdentity.ToolName,
            ToolVersion: LibraryToolIdentity.ToolVersion(libraryPins),
            ToolSha256: LibraryToolIdentity.ComputeToolSha256(libraryPins));
        var workflow = new NativeRecoveryWorkflow(provider);

        var record = await workflow.RecoverAsync(request, executionContext, cancellationToken);

        output.WriteLine($"Recovery status: {record.Status}");
        output.WriteLine($"Failure message: {record.FailureMessage}");
        output.WriteLine($"Edge count: {record.Edges.Count}");
        foreach (var evidence in record.MappingEvidence)
        {
            output.WriteLine($"  mapping evidence: {evidence}");
        }

        if (record.Status != NativeRecoveryStatus.Recovered)
        {
            output.WriteLine(
                "MISMATCH: the index-parsed managed descriptor did not resolve against LibCpp2IL's " +
                "candidates. Index-parsed identity: " +
                $"{descriptor.DeclaringTypeFullName}::{descriptor.MethodName}({string.Join(",", descriptor.ParameterTypeFullNames)})");
        }

        Assert.Equal(NativeRecoveryStatus.Recovered, record.Status);
        Assert.True(record.Edges.Count >= 1, "Expected at least one recovered native evidence edge.");
    }
}
