using Xunit;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Paths;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class IndexingWorkflowTests
{
    [Fact]
    public void Index_identity_is_deterministic_and_lower_hex()
    {
        var first = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 8);
        var second = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 8);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain(first, char.IsUpper);
        Assert.Equal(9, S1Atlas.Indexing.Workflow.IndexingWorkflow.IndexSchemaVersion);
    }

    [Fact]
    public void Interop_identity_uses_content_hash_and_not_machine_local_path()
    {
        var first = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 9, "interop-hash");
        var sameBytesAtAnotherPath = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 9, "interop-hash");
        var changedBytes = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 9, "different-hash");

        Assert.Equal(first, sameBytesAtAnotherPath);
        Assert.NotEqual(first, changedBytes);
    }

    [Fact]
    public async Task Schedule_one_index_is_reused_after_a_completed_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('a', 64);
        var extractionId = new string('b', 64);
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()));

            var first = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);
            var second = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);

            Assert.False(first.Reused);
            Assert.True(second.Reused);
            Assert.True(File.Exists(OwnedIndexPaths.ForScheduleOne(root, buildId, first.IndexId).CompleteMarkerPath));
            Assert.True(first.SymbolCount > 0);
            var sourceLocations = await repository.GetCompletedSourceLocationsAsync(first.IndexId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(sourceLocations);
            Assert.All(sourceLocations, location =>
            {
                Assert.NotNull(location.EndLine);
                Assert.NotNull(location.EndColumn);
            });
            Assert.Contains(sourceLocations, location => location.StartColumn > 1);

            var symbols = await repository.GetCompletedSymbolsAsync(first.IndexId, TestContext.Current.CancellationToken);
            Assert.Equal(
                BodyRecoveryStatus.Recovered,
                Assert.Single(
                    symbols,
                    symbol => symbol.Signature.Contains("DerivedFixture::Overload(System.Int32)", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.Equal(
                BodyRecoveryStatus.StubOrUnavailable,
                Assert.Single(symbols, symbol => symbol.Signature.Contains("FixtureRoot::GetValue", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.Equal(
                BodyRecoveryStatus.NoBodyByDesign,
                Assert.Single(symbols, symbol => symbol.Signature.Contains("IFixtureContract::get_ContractValue", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.All(
                symbols.Where(symbol => symbol.Kind is "Type" or "Field" or "Property" or "Event"),
                symbol => Assert.Null(symbol.BodyRecoveryStatus));

            var forced = await workflow.RunScheduleOneAsync(buildId, true, TestContext.Current.CancellationToken);
            Assert.False(forced.Reused);
            Assert.NotEqual(first.IndexId, forced.IndexId);
            Assert.NotEqual(first.SnapshotId, forced.SnapshotId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Schedule_one_index_persists_callable_surface_from_explicit_interop_assembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-interop-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var interopDirectory = Path.Combine(root, "interop");
        var interopPath = Path.Combine(interopDirectory, "Il2CppAssemblies", "Assembly-CSharp.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(interopPath)!);
        File.Copy(typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location, interopPath);
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('c', 64);
        var extractionId = new string('d', 64);
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()));

            var result = await workflow.RunScheduleOneAsync(
                buildId,
                false,
                TestContext.Current.CancellationToken,
                interopPath);

            Assert.True(result.CallableSurfaceCount > 0);
            Assert.Empty(result.Warnings);
            Assert.Equal(2, result.SourceFileCount);
            var callable = await repository.GetCompletedCallableSurfaceAsync(result.IndexId, TestContext.Current.CancellationToken);
            var setOccupant = Assert.Single(callable, record => record.GameCanonicalKey.Contains("SetOccupant", StringComparison.Ordinal));
            Assert.Equal(CallableSurfaceStatus.Resolved, setOccupant.Status);
            Assert.Equal(InteropInputTrust.LocalOnly, setOccupant.InteropInputTrust);
            Assert.False(setOccupant.RequiresReflection);

            var directoryResult = await workflow.RunScheduleOneAsync(
                buildId,
                true,
                TestContext.Current.CancellationToken,
                interopDirectory);
            Assert.True(directoryResult.CallableSurfaceCount > 0);
            Assert.Equal(2, directoryResult.SourceFileCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Schedule_one_index_discovers_interop_from_the_persisted_installation_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-default-interop-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        var interopPath = Path.Combine(root, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        Directory.CreateDirectory(Path.GetDirectoryName(interopPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        File.Copy(typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location, interopPath);
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('e', 64);
        var extractionId = new string('f', 64);
        var now = DateTimeOffset.UtcNow;
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, now, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, now, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            await repository.SaveSnapshotAsync(
                new EnvironmentSnapshot(
                    2,
                    new GameBuild(buildId, new string('1', 64), new string('2', 64), now, true),
                    new InstallationObservation("2022.3.62", "3164500", "fixture", root, null, null),
                    [
                        new DependencyVersion(DependencyKind.S1Api, null, null, false),
                        new DependencyVersion(DependencyKind.S1Mapi, null, null, false),
                        new DependencyVersion(DependencyKind.MelonLoader, null, null, false),
                        new DependencyVersion(DependencyKind.Sideload, null, null, false)
                    ],
                    "0.1.0-test",
                    now),
                TestContext.Current.CancellationToken);
            var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()),
                repository);

            var result = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);

            Assert.True(result.CallableSurfaceCount > 0);
            Assert.Empty(result.Warnings);
            Assert.Equal(2, result.SourceFileCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
