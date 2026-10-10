using System.Security.Cryptography;
using Xunit;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.ModChecking;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;

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
        Assert.Equal(17, S1Atlas.Indexing.Workflow.IndexingWorkflow.IndexSchemaVersion);
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
            await TestDirectory.DeleteTreeAsync(root);
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
            await TestDirectory.DeleteTreeAsync(root);
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
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Installed_hud_fields_resolve_and_missing_interop_is_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-hud-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var (workflow, repository, authority, interopPath) = await CreateInstalledHudWorkflowAsync(root, cancellationToken);
            var buildId = authority.BuildId;
            var missing = await workflow.RunScheduleOneAsync(buildId, false, cancellationToken);
            Assert.False(missing.Reused);
            var missingRows = await repository.GetCompletedCallableSurfaceAsync(missing.IndexId, cancellationToken);
            var hudFields = new[]
            {
                (Name: "topScreenText", GameType: "TMPro.TextMeshProUGUI", InteropType: "Il2CppTMPro.TextMeshProUGUI"),
                (Name: "topScreenText_Background", GameType: "UnityEngine.RectTransform", InteropType: "Il2CppUnityEngine.RectTransform")
            };
            foreach (var field in hudFields)
            {
                var key = SymbolIdentity.Create(
                    CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Field,
                    $"ScheduleOne.UI.HUD::{field.GameType} {field.Name}").CanonicalKey;
                var row = Assert.Single(missingRows, row => row.GameCanonicalKey == key);
                Assert.Equal(CallableSurfaceStatus.Unknown, row.Status);
                Assert.Null(row.InteropSignature);
                Assert.Null(row.InteropInputSha256);
            }
            Assert.Equal(
                "InteropSurfaceUnknown: no usable Il2CppInterop Assembly-CSharp.dll was found; wrapper-dependent availability is unknown.",
                Assert.Single(missing.Warnings));

            Directory.CreateDirectory(Path.GetDirectoryName(interopPath)!);
            File.Copy(typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location, interopPath);
            var interopHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(interopPath, cancellationToken))).ToLowerInvariant();
            var installed = await workflow.RunScheduleOneAsync(buildId, false, cancellationToken);

            Assert.False(installed.Reused);
            Assert.NotEqual(missing.IndexId, installed.IndexId);
            Assert.Empty(installed.Warnings);
            var installedRows = await repository.GetCompletedCallableSurfaceAsync(installed.IndexId, cancellationToken);
            foreach (var field in hudFields)
            {
                var key = SymbolIdentity.Create(
                    CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Field,
                    $"ScheduleOne.UI.HUD::{field.GameType} {field.Name}").CanonicalKey;
                var row = Assert.Single(installedRows, row => row.GameCanonicalKey == key);
                Assert.Equal(CallableSurfaceStatus.Resolved, row.Status);
                Assert.Equal(CallableSurfaceKind.PublicPropertyAccessor, row.Kind);
                Assert.Equal($"{field.InteropType} {field.Name}", row.InteropSignature);
                Assert.Equal(interopHash, row.InteropInputSha256);
                Assert.False(row.RequiresReflection);
                Assert.Equal(InteropInputTrust.LocalOnly, row.InteropInputTrust);
            }
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Previous_index_identity_is_rebuilt()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-hud-rebuild-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var (workflow, repository, authority, interopPath) = await CreateInstalledHudWorkflowAsync(root, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(interopPath)!);
            File.Copy(typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location, interopPath);
            var interopHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(interopPath, cancellationToken))).ToLowerInvariant();
            var extractionId = authority.Extraction.ExtractionId;
            var previousIndexId = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
                extractionId,
                S1Atlas.Indexing.Workflow.IndexingWorkflow.DecompilerPackage,
                S1Atlas.Indexing.Workflow.IndexingWorkflow.DecompilerVersion,
                "default", 15, interopHash);
            var previousSnapshotId = "schedule-i:" + extractionId + ":" + previousIndexId;
            var now = DateTimeOffset.UtcNow.ToString("O");
            const string qualifiedName = "ScheduleOne.UI.HUD::TMPro.TextMeshProUGUI topScreenText";
            var identity = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Field, qualifiedName);
            var symbol = new IndexSymbolRecord(
                identity.Fingerprint(), previousSnapshotId, identity.CanonicalKey, "Field",
                qualifiedName, "TMPro.TextMeshProUGUI topScreenText", false);
            var previousRow = new IndexCallableSurfaceRecord(
                "previous-hud-surface", previousIndexId, previousSnapshotId, symbol.SymbolId, symbol.CanonicalKey,
                "Assembly-CSharp.dll", interopHash, null, CallableSurfaceKind.NonPublicWrapper, false,
                CallableSurfaceStatus.Unavailable, InteropInputTrust.LocalOnly, "no usable interop wrapper or accessor was found");
            await repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(previousSnapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, now),
                cancellationToken);
            await repository.StartIndexRunAsync(
                new IndexRunRecord(previousIndexId, previousSnapshotId, IndexRunStatus.Running, now), cancellationToken);
            await repository.CompleteIndexRunAsync(
                previousIndexId, new IndexWriteSet([symbol], [], [], [], [], [previousRow]), now, cancellationToken);
            Assert.NotNull(await repository.GetCompletedIndexAsync(previousIndexId, cancellationToken));

            var result = await workflow.RunScheduleOneAsync(authority.BuildId, false, cancellationToken);

            Assert.False(result.Reused);
            Assert.NotEqual(previousIndexId, result.IndexId);
            var rebuiltRows = await repository.GetCompletedCallableSurfaceAsync(result.IndexId, cancellationToken);
            var rebuilt = Assert.Single(rebuiltRows, row => row.GameCanonicalKey == symbol.CanonicalKey);
            Assert.Equal(CallableSurfaceStatus.Resolved, rebuilt.Status);
            Assert.Equal(CallableSurfaceKind.PublicPropertyAccessor, rebuilt.Kind);
            Assert.Equal("Il2CppTMPro.TextMeshProUGUI topScreenText", rebuilt.InteropSignature);
            Assert.Equal(interopHash, rebuilt.InteropInputSha256);
            Assert.False(rebuilt.RequiresReflection);
            Assert.Equal(InteropInputTrust.LocalOnly, rebuilt.InteropInputTrust);
            Assert.Equal(previousRow, Assert.Single(await repository.GetCompletedCallableSurfaceAsync(previousIndexId, cancellationToken)));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    private static async Task<(
        S1Atlas.Indexing.Workflow.IndexingWorkflow Workflow,
        SqliteAtlasRepository Repository,
        PreferredVerifiedExtraction Authority,
        string InteropPath)> CreateInstalledHudWorkflowAsync(string root, CancellationToken cancellationToken)
    {
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('3', 64);
        var extractionId = new string('4', 64);
        var now = DateTimeOffset.UtcNow;
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, now, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, now, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));
        await repository.InitializeAsync(cancellationToken);
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
            cancellationToken);
        var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
            root, repository, (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
            new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()), repository);
        return (workflow, repository, authority, Path.Combine(root, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll"));
    }

    // SceneIndexWorkflow.RequireCodeIndexAsync rejects a completed Schedule I
    // Installed code snapshot whose EnvironmentSnapshotId is empty. The writer here
    // previously created the CodeSnapshotRecord with only 5 positional arguments,
    // leaving EnvironmentSnapshotId permanently null for every Schedule I Installed
    // index even though the current environment snapshot matched the requested build.
    // This regression test pins the writer to populate EnvironmentSnapshotId from the
    // current, build-matching environment snapshot so the scene-index identity gate
    // can be satisfied by a legitimately completed code index.
    [Fact]
    public async Task Schedule_one_code_snapshot_records_the_current_environment_snapshot_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-envid-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('7', 64);
        var extractionId = new string('8', 64);
        var now = DateTimeOffset.UtcNow;
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, now, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, now, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var environmentSnapshot = new EnvironmentSnapshot(
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
                now);
            await repository.SaveSnapshotAsync(environmentSnapshot, TestContext.Current.CancellationToken);
            var expectedEnvironmentSnapshotId = EnvironmentSnapshotId.Create(environmentSnapshot);
            var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()),
                repository);

            var result = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);

            var snapshot = await repository.GetCodeSnapshotAsync(result.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.False(string.IsNullOrWhiteSpace(snapshot!.EnvironmentSnapshotId));
            Assert.Equal(expectedEnvironmentSnapshotId, snapshot.EnvironmentSnapshotId);
            Assert.Equal(extractionId, snapshot.SourceIdentity);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    // a code snapshot completed before this fix (or completed without a
    // resolvable environment) is healed in place the next time RunScheduleOneAsync
    // reuses it, once an environment snapshot for the same build becomes available.
    [Fact]
    public async Task Schedule_one_reuse_heals_a_previously_null_environment_snapshot_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-heal-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('9', 64);
        var extractionId = new string('a', 64);
        var now = DateTimeOffset.UtcNow;
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, now, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, now, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);

            // First run: no atlasRepository wired in, matching the historical behavior
            // that left environment_snapshot_id null for every Schedule I Installed row.
            var unhealedWorkflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()));
            var first = await unhealedWorkflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);
            var beforeHeal = await repository.GetCodeSnapshotAsync(first.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(beforeHeal);
            Assert.Null(beforeHeal!.EnvironmentSnapshotId);

            var environmentSnapshot = new EnvironmentSnapshot(
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
                now);
            await repository.SaveSnapshotAsync(environmentSnapshot, TestContext.Current.CancellationToken);
            var expectedEnvironmentSnapshotId = EnvironmentSnapshotId.Create(environmentSnapshot);

            var healedWorkflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()),
                repository);
            var second = await healedWorkflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);
            Assert.True(second.Reused);
            Assert.Equal(first.SnapshotId, second.SnapshotId);

            var afterHeal = await repository.GetCodeSnapshotAsync(second.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(afterHeal);
            Assert.Equal(expectedEnvironmentSnapshotId, afterHeal!.EnvironmentSnapshotId);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Schedule_one_index_covers_every_game_owned_assembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-assemblies-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extractions", "current");
        WriteGameAssemblies(extractionRoot);
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('1', 64);
        var authority = CreateAuthority(buildId, new string('2', 64), extractionRoot);
        var ct = TestContext.Current.CancellationToken;

        try
        {
            await repository.InitializeAsync(ct);
            var workflow = new IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new ScheduleOneIndexSource(new IlSpyManagedDecompiler()));

            var result = await workflow.RunScheduleOneAsync(buildId, false, ct);

            var symbols = await repository.GetCompletedSymbolsAsync(result.IndexId, ct);
            var baseDefinition = Assert.Single(symbols, symbol => symbol.Kind == "Type" && symbol.Signature == "ScheduleOne.Core.Items.Framework.BaseItemDefinition");
            Assert.Contains(symbols, symbol => symbol.Kind == "Type" && symbol.Signature == "ScheduleOne.Core.Avatar.AvatarObject");
            Assert.Contains(symbols, symbol => symbol.Kind == "Method" && symbol.QualifiedName.StartsWith("ScheduleOne.Core.Items.Framework.BaseItemInstance::GetMonetaryValue(", StringComparison.Ordinal));
            Assert.DoesNotContain(symbols, symbol => symbol.QualifiedName.StartsWith("Vendor.", StringComparison.Ordinal));

            var itemDefinition = Assert.Single(symbols, symbol => symbol.Kind == "Type" && symbol.Signature == "ScheduleOne.ItemFramework.ItemDefinition");
            var relationships = await repository.GetCompletedRelationshipsAsync(result.IndexId, ct);
            Assert.Contains(relationships, relationship =>
                relationship.SourceSymbolId == itemDefinition.SymbolId &&
                relationship.Kind == "Inherits" &&
                relationship.TargetSymbolId == baseDefinition.SymbolId);

            var sourceFiles = await repository.GetCompletedSourceFilesAsync(result.IndexId, ct);
            Assert.Equal(
                new[] { "Assembly-CSharp.cs", "ScheduleOne.Core.cs" },
                sourceFiles.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray());
            var coreFile = Assert.Single(sourceFiles, file => file.RelativePath == "ScheduleOne.Core.cs");
            var locations = await repository.GetCompletedSourceLocationsAsync(result.IndexId, ct);
            Assert.Contains(locations, location => location.SymbolId == baseDefinition.SymbolId && location.SourceFileId == coreFile.SourceFileId);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Check_mod_reports_second_assembly_dependencies_as_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-check-mod-" + Guid.NewGuid().ToString("N"));
        var fromBuild = new string('3', 64);
        var toBuild = new string('5', 64);
        var authorities = new Dictionary<string, PreferredVerifiedExtraction>(StringComparer.Ordinal)
        {
            [fromBuild] = CreateAuthority(fromBuild, new string('4', 64), Path.Combine(root, "extractions", "from")),
            [toBuild] = CreateAuthority(toBuild, new string('6', 64), Path.Combine(root, "extractions", "to"))
        };
        WriteGameAssemblies(authorities[fromBuild].Extraction.RootPath);
        WriteGameAssemblies(authorities[toBuild].Extraction.RootPath);
        var interopCore = Path.Combine(root, "compile-references", "Il2CppScheduleOne.Core.dll");
        ScratchAssembly.Compile(InteropCoreSource, interopCore);
        var modPath = Path.Combine(root, "mods", "CoreDependentMod.dll");
        ScratchAssembly.Compile(ModSource, modPath, interopCore);
        var databasePath = Path.Combine(root, "atlas.db");
        var repository = new SqliteAtlasRepository(databasePath);
        var ct = TestContext.Current.CancellationToken;

        try
        {
            await repository.InitializeAsync(ct);
            var workflow = new IndexingWorkflow(
                root,
                repository,
                (buildId, _) => Task.FromResult<PreferredVerifiedExtraction?>(authorities[buildId]),
                new ScheduleOneIndexSource(new IlSpyManagedDecompiler()));
            var from = await workflow.RunScheduleOneAsync(fromBuild, false, ct);
            var to = await workflow.RunScheduleOneAsync(toBuild, false, ct);
            var reader = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(databasePath));

            var check = await new ModCheckService(reader, reader, new IlSpyManagedDecompiler())
                .CheckAsync(modPath, fromBuild, toBuild, from.IndexId, to.IndexId, ct);

            var core = check.Dependencies.Where(dependency => dependency.Name.StartsWith("ScheduleOne.Core.", StringComparison.Ordinal)).ToArray();
            Assert.Contains(core, dependency => dependency.Kind == "Type" && dependency.Name == "ScheduleOne.Core.Items.Framework.BaseItemDefinition");
            Assert.Contains(core, dependency => dependency.Kind == "Type" && dependency.Name == "ScheduleOne.Core.Items.Framework.BaseItemInstance");
            Assert.Contains(core, dependency => dependency.Kind == "Type" && dependency.Name == "ScheduleOne.Core.Avatar.AvatarObject");
            Assert.Contains(core, dependency => dependency.Name.Contains("GetMonetaryValue", StringComparison.Ordinal));
            Assert.All(core, dependency => Assert.Equal("unchanged", dependency.Status));
            Assert.DoesNotContain(check.Dependencies, dependency => dependency.Reason == HarmonyPatchReasons.TargetTypeNotFound);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Schedule_one_index_maps_each_game_assembly_to_its_own_interop_assembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-multi-interop-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extractions", "current");
        WriteGameAssemblies(extractionRoot);
        var melonLoader = Path.Combine(root, "MelonLoader");
        var interopDirectory = Path.Combine(melonLoader, "Il2CppAssemblies");
        ScratchAssembly.Compile(InteropGameSource, Path.Combine(interopDirectory, "Assembly-CSharp.dll"));
        var interopCore = Path.Combine(interopDirectory, "Il2CppScheduleOne.Core.dll");
        ScratchAssembly.Compile(InteropCoreSource, interopCore);
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('7', 64);
        var authority = CreateAuthority(buildId, new string('8', 64), extractionRoot);
        var ct = TestContext.Current.CancellationToken;

        try
        {
            await repository.InitializeAsync(ct);
            var workflow = new IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new ScheduleOneIndexSource(new IlSpyManagedDecompiler()));
            var coreHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(interopCore, ct))).ToLowerInvariant();

            var result = await workflow.RunScheduleOneAsync(buildId, false, ct, melonLoader);

            Assert.Empty(result.Warnings);
            var sourceFiles = await repository.GetCompletedSourceFilesAsync(result.IndexId, ct);
            Assert.Contains(sourceFiles, file => file.RelativePath == "interop/ScheduleOne.Core.cs");
            var calculate = Assert.Single(
                await repository.GetCompletedCallableSurfaceAsync(result.IndexId, ct),
                record => record.GameCanonicalKey.Contains("BaseItemInstance::CalculateValue", StringComparison.Ordinal));
            Assert.Equal(CallableSurfaceStatus.Resolved, calculate.Status);
            Assert.Equal("Il2CppScheduleOne.Core.dll", calculate.InteropAssemblyName);
            Assert.Equal(coreHash, calculate.InteropInputSha256);

            File.Delete(interopCore);
            var withoutCore = await workflow.RunScheduleOneAsync(buildId, false, ct, melonLoader);

            Assert.NotEqual(result.IndexId, withoutCore.IndexId);
            Assert.Contains(withoutCore.Warnings, warning => warning.Contains("Il2CppScheduleOne.Core.dll", StringComparison.Ordinal));
            var unknown = Assert.Single(
                await repository.GetCompletedCallableSurfaceAsync(withoutCore.IndexId, ct),
                record => record.GameCanonicalKey.Contains("BaseItemInstance::CalculateValue", StringComparison.Ordinal));
            Assert.Equal(CallableSurfaceStatus.Unknown, unknown.Status);
            Assert.Equal("Il2CppScheduleOne.Core.dll", unknown.InteropAssemblyName);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    private const string InteropGameSource = """
        namespace Il2CppScheduleOne.ItemFramework { public class ItemDefinition { } }
        """;

    private static PreferredVerifiedExtraction CreateAuthority(string buildId, string extractionId, string extractionRoot) =>
        new(
            buildId,
            new PreferredExtraction(buildId, extractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

    private static void WriteGameAssemblies(string extractionRoot)
    {
        var reconstructed = Path.Combine(extractionRoot, "reconstructed");
        var core = Path.Combine(reconstructed, "ScheduleOne.Core.dll");
        ScratchAssembly.Compile(CoreSource, core);
        ScratchAssembly.Compile(GameSource, Path.Combine(reconstructed, "Assembly-CSharp.dll"), core);
        ScratchAssembly.Compile(VendorSource, Path.Combine(reconstructed, "Vendor.Library.dll"));
    }

    private const string CoreSource = """
        namespace ScheduleOne.Core.Items.Framework {
          public abstract class BaseItemDefinition { public string ID { get; set; } }
          public abstract class BaseItemInstance {
            public string ID => "item";
            private float CalculateValue() => 1f;
            public virtual float GetMonetaryValue() => CalculateValue();
          }
        }
        namespace ScheduleOne.Core.Avatar { public class AvatarObject { } }
        """;

    private const string GameSource = """
        namespace ScheduleOne.ItemFramework {
          public class ItemDefinition : ScheduleOne.Core.Items.Framework.BaseItemDefinition { }
        }
        """;

    private const string VendorSource = """
        namespace Vendor.Library { public sealed class Helper { } }
        """;

    private const string InteropCoreSource = """
        namespace Il2CppScheduleOne.Core.Items.Framework {
          public class BaseItemDefinition { public string ID { get => null; set { } } }
          public class BaseItemInstance {
            public string ID => null;
            public float CalculateValue() => 0f;
            public float GetMonetaryValue() => 0f;
          }
        }
        namespace Il2CppScheduleOne.Core.Avatar { public class AvatarObject { } }
        """;

    private const string ModSource = """
        namespace CoreDependentMod {
          public static class Plugin {
            public static Il2CppScheduleOne.Core.Avatar.AvatarObject Avatar;
            public static string DefinitionId(Il2CppScheduleOne.Core.Items.Framework.BaseItemDefinition definition) => definition.ID;
            public static float Value(Il2CppScheduleOne.Core.Items.Framework.BaseItemInstance instance) =>
              instance.GetMonetaryValue() + instance.ID.Length;
          }
        }
        """;
}
