using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Scene;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneIndexWorkflowTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly string _buildId = new('a', 64);
    private readonly string _extractionId = new('b', 64);
    private readonly string _inputId = new('c', 64);
    private readonly string _codeIndexId = new('d', 64);
    private readonly string _codeSnapshotId = new('e', 64);

    [Fact]
    public async Task Missing_preferred_verified_extraction_fails_before_filesystem_or_database_work()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(null));

        var exception = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.NoPreferredVerifiedExtraction, exception.Status);
        Assert.Empty(repository.CreatedSnapshots);
    }

    [Fact]
    public async Task Replay_unverified_extraction_input_is_rejected_before_parsing()
    {
        var repository = CreateRepository(replayVerified: false);
        var workflow = CreateWorkflow(repository, Authority());

        var exception = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.NoReplayVerifiedExtractionInput, exception.Status);
        Assert.Equal(0, repository.ParserCalls);
    }

    [Fact]
    public async Task Cross_build_code_index_is_rejected_before_a_scene_snapshot_starts()
    {
        var repository = CreateRepository(replayVerified: true);
        repository.CodeSnapshot = repository.CodeSnapshot with { SourceIdentity = new string('f', 64) };
        var workflow = CreateWorkflow(repository, Authority());

        var exception = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.CrossBuildCodeIndex, exception.Status);
        Assert.Empty(repository.CreatedSnapshots);
    }

    [Fact]
    public async Task Parser_failure_marks_the_started_snapshot_failed_and_removes_owned_staging()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (_, _) => throw new InvalidDataException("class-id probe failed"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Contains(snapshot.SceneSnapshotId, repository.FailedSnapshotIds);
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).StagingRoot));
    }

    [Fact]
    public async Task Canceled_parse_marks_the_started_snapshot_failed_and_removes_owned_staging()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (_, cancellationToken) => throw new OperationCanceledException(cancellationToken));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Contains(snapshot.SceneSnapshotId, repository.FailedSnapshotIds);
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).StagingRoot));
    }

    [Fact]
    public async Task Database_rollback_leaves_no_completed_snapshot_or_owned_staging()
    {
        var repository = CreateRepository(replayVerified: true);
        repository.ThrowOnComplete = true;
        var workflow = CreateWorkflow(repository, Authority());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Null(repository.CompletedSnapshot);
        Assert.Contains(snapshot.SceneSnapshotId, repository.FailedSnapshotIds);
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).StagingRoot));
    }

    [Fact]
    public async Task Changed_scene_input_hash_is_rejected_before_database_completion()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) =>
        {
            using (var stream = new FileStream(containers[0].PrimaryPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x00);
            }
            return Parsed(containers);
        });

        await Assert.ThrowsAsync<IOException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Null(repository.CompletedSnapshot);
        Assert.Contains(Assert.Single(repository.CreatedSnapshots).SceneSnapshotId, repository.FailedSnapshotIds);
    }

    [Fact]
    public async Task Preferred_extraction_change_after_parsing_is_rejected_before_database_completion()
    {
        var repository = CreateRepository(replayVerified: true);
        var calls = 0;
        var workflow = CreateWorkflow(repository, (_, _) =>
        {
            calls++;
            return calls == 1
                ? Authority()(_buildId, TestContext.Current.CancellationToken)
                : Task.FromResult<PreferredVerifiedExtraction?>(new(
                    _buildId,
                    new PreferredExtraction(_buildId, new string('f', 64), DateTimeOffset.UnixEpoch, ExtractionPreferenceReason.ManualPromotion),
                    new ValidatedExtraction(new string('f', 64), "recipe", _buildId, "tool", "attempt", "profile", 1, "profile", 1, 1,
                        new string('4', 64), "validated", DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid,
                        new ExtractionStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []))));
        });

        var exception = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.PreferredExtractionChanged, exception.Status);
        Assert.Null(repository.CompletedSnapshot);
    }

    [Fact]
    public async Task Changed_code_index_or_parser_version_is_rejected_before_database_completion()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) =>
        {
            repository.IndexRun = repository.IndexRun with { IndexId = new string('f', 64) };
            return Parsed(containers);
        });

        var exception = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.CodeIndexChanged, exception.Status);
        Assert.Null(repository.CompletedSnapshot);

        repository = CreateRepository(replayVerified: true);
        workflow = CreateWorkflow(repository, Authority(), (containers, _) =>
            Parsed(containers).Select(container => container with { SerializedFileVersion = 21 }).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));
        Assert.Null(repository.CompletedSnapshot);
    }

    [Fact]
    public async Task Failure_after_database_completion_never_publishes_or_reuses_the_snapshot()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) =>
        {
            var staging = Directory.GetDirectories(_root, "*.staging", SearchOption.AllDirectories).Single();
            Directory.CreateDirectory(Path.Combine(staging, "complete.marker"));
            return Parsed(containers);
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.DoesNotContain(snapshot.SceneSnapshotId, repository.PublishedSnapshotIds);
        Assert.Contains(snapshot.SceneSnapshotId, repository.FailedSnapshotIds);
        Assert.Null(await repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).FinalRoot));
    }

    [Fact]
    public async Task Start_failure_marks_the_created_snapshot_failed_and_removes_owned_staging()
    {
        var repository = CreateRepository(replayVerified: true);
        repository.ThrowOnStart = true;
        var workflow = CreateWorkflow(repository, Authority());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Contains(snapshot.SceneSnapshotId, repository.FailedSnapshotIds);
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).StagingRoot));
    }

    [Fact]
    public async Task Every_allowlisted_primary_includes_matching_resource_sidecars()
    {
        var repository = CreateRepository(replayVerified: true);
        var installRoot = repository.Environment.Installation.InstallationRoot!;
        Directory.CreateDirectory(Path.Combine(installRoot, "Schedule I_Data"));
        File.WriteAllBytes(Path.Combine(installRoot, "Schedule I_Data", "level0.resource"), [1]);
        var workflow = CreateWorkflow(repository, Authority());

        await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.Contains(Path.Combine(installRoot, "Schedule I_Data", "level0.resource"), Assert.Single(repository.LastParsedContainers).SidecarPaths);
    }

    [Fact]
    public async Task Unsupported_unity_version_is_rejected_before_parsing()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), unityVersion: "2022.3.61f1");

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.UnsupportedContainer, failure.Status);

        Assert.Equal(0, repository.ParserCalls);
    }

    [Fact]
    public async Task Unity_version_with_supported_prefix_but_different_patch_is_rejected_before_parsing()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), unityVersion: "2022.3.620f1");

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.UnsupportedContainer, failure.Status);

        Assert.Equal(0, repository.ParserCalls);
    }

    [Fact]
    public async Task Stripped_type_tree_container_fails_with_the_container_named_and_is_never_completed()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) => containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion,
            [
                new ParsedSceneObject(1, 1, 128, 64, ParsedSceneObjectKind.GameObject, [], null, null, null, null, null),
                new ParsedSceneObject(2, 4, 192, 64, ParsedSceneObjectKind.Transform, [], null, null, null, null, null)
            ],
            [], false, TypeTreeEmbedded: false)).ToArray());

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.SceneTypeTreeUnavailable, failure.Status);
        Assert.Contains("'Schedule I_Data/level0'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("TypeTreeEnabled=false", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no pinned Unity class database is configured", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tools install unity-classdata", failure.Message, StringComparison.Ordinal);
        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Null(repository.CompletedSnapshot);
        Assert.Empty(repository.PublishedSnapshotIds);
        Assert.Equal("SceneTypeTreeUnavailable", repository.FailureCodes[snapshot.SceneSnapshotId]);
        Assert.False(Directory.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, snapshot.SceneSnapshotId).StagingRoot));
    }

    // a configured class database that is not installed (or holds no dump) still fails
    // the run, but the message now names the pin and the install command.
    [Fact]
    public async Task Stripped_type_tree_container_with_uninstalled_class_database_names_the_pin()
    {
        var repository = CreateRepository(replayVerified: true);
        var descriptor = new UnityClassDatabaseDescriptor("unity-classdata", "uabea-5adb448", new string('d', 64));
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) => containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion,
            [new ParsedSceneObject(1, 1, 128, 64, ParsedSceneObjectKind.GameObject, [], null, null, null, null, null)],
            [], false, TypeTreeEmbedded: false, TypeTreeSource: ParsedTypeTreeSource.Unavailable)).ToArray(), classDatabase: descriptor);

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.SceneTypeTreeUnavailable, failure.Status);
        Assert.Contains("unity-classdata uabea-5adb448 (sha256:" + new string('d', 64) + ") is not installed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tools install unity-classdata", failure.Message, StringComparison.Ordinal);
        Assert.Null(repository.CompletedSnapshot);
    }

    // a stripped container decoded through the pinned class database completes, and the
    // snapshot records which type-tree source (package, version, hash, substituted dump) did it.
    [Fact]
    public async Task Stripped_type_tree_container_decoded_with_class_database_completes_and_records_provenance()
    {
        var repository = CreateRepository(replayVerified: true);
        var descriptor = new UnityClassDatabaseDescriptor("unity-classdata", "uabea-5adb448", new string('d', 64));
        var source = ParsedTypeTreeSource.FromClassDatabase(descriptor, "2022.3.26f1", exactVersionMatch: false);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) => containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion,
            [
                new ParsedSceneObject(1, 1, 128, 64, ParsedSceneObjectKind.GameObject, [],
                    new ParsedGameObjectData("Recovered Root", 0, 0, true, [new ParsedScenePPtr(0, 2)]), null, null, null, null),
                new ParsedSceneObject(2, 4, 192, 64, ParsedSceneObjectKind.Transform, [], null,
                    new ParsedTransformData(new ParsedScenePPtr(0, 1), new ParsedScenePPtr(0, 0), [], new ParsedSceneVector3(1, 2, 3), new ParsedSceneQuaternion(0, 0, 0, 1), new ParsedSceneVector3(1, 1, 1), 0),
                    null, null, null)
            ],
            [], false, TypeTreeEmbedded: false, TypeTreeSource: source)).ToArray(), classDatabase: descriptor);

        var result = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.False(result.Reused);
        Assert.Equal(1, result.GameObjectCount);
        const string expectedLabel = "class-database unity-classdata uabea-5adb448 sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd (2022.3.26f1 layouts for 2022.3.62f1; nearest earlier dump)";
        Assert.Equal(expectedLabel, result.TypeTreeSource);
        Assert.Equal(expectedLabel, repository.CompletedSnapshot!.TypeTreeSource);
        Assert.Single(repository.PublishedSnapshotIds);

        var reused = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);
        Assert.True(reused.Reused);
        Assert.Equal(expectedLabel, reused.TypeTreeSource);
    }

    [Fact]
    public async Task Class_database_pin_is_part_of_the_scene_snapshot_identity()
    {
        var repository = CreateRepository(replayVerified: true);
        var descriptor = new UnityClassDatabaseDescriptor("unity-classdata", "uabea-5adb448", new string('d', 64));
        var without = CreateWorkflow(repository, Authority());
        var first = await without.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);
        var with = CreateWorkflow(repository, Authority(), classDatabase: descriptor);

        var second = await with.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.SceneSnapshotId, second.SceneSnapshotId);
        Assert.False(second.Reused);
        Assert.Equal("embedded", second.TypeTreeSource);
    }

    [Fact]
    public void Type_tree_source_label_is_deduplicated_sorted_and_bounded()
    {
        var descriptor = new UnityClassDatabaseDescriptor("unity-classdata", "uabea-5adb448", new string('d', 64));
        ParsedSceneContainer Container(string path, ParsedTypeTreeSource? source, bool embedded) =>
            new(path, "C:/secret/" + path, [], new string('e', 64), "2022.3.62f2", 22, [], [], false, embedded, source);

        var label = SceneIndexWorkflow.TypeTreeSourceLabel(
        [
            Container("b", ParsedTypeTreeSource.FromClassDatabase(descriptor, "2022.3.26f1", false), false),
            Container("a", null, true),
            Container("c", ParsedTypeTreeSource.FromClassDatabase(descriptor, "2022.3.26f1", false), false),
            Container("d", null, false)
        ]);

        Assert.Equal(
            "class-database unity-classdata uabea-5adb448 sha256:" + new string('d', 64) + " (2022.3.26f1 layouts for 2022.3.62f2; nearest earlier dump); embedded; unavailable",
            label);
        Assert.DoesNotContain("secret", label, StringComparison.Ordinal);
        Assert.Equal("embedded", SceneIndexWorkflow.TypeTreeSourceLabel([]));
    }

    [Fact]
    public async Task Stripped_type_tree_container_without_supported_objects_is_not_a_failure()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) => containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion,
            [new ParsedSceneObject(7, 23, 128, 64, ParsedSceneObjectKind.Other, [], null, null, null, null, null)],
            [], false, TypeTreeEmbedded: false)).ToArray());

        var result = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.False(result.Reused);
        Assert.Equal(0, result.GameObjectCount);
    }

    [Fact]
    public async Task Write_set_with_object_table_entries_but_no_game_object_fails_instead_of_completing()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) => containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion,
            [new ParsedSceneObject(1, 1, 128, 64, ParsedSceneObjectKind.GameObject, [], null, null, null, null, null)],
            [], false)).ToArray());

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.NoRecoverableSceneObjects, failure.Status);
        var snapshot = Assert.Single(repository.CreatedSnapshots);
        Assert.Null(repository.CompletedSnapshot);
        Assert.Equal("NoRecoverableSceneObjects", repository.FailureCodes[snapshot.SceneSnapshotId]);
    }

    [Fact]
    public async Task Completed_snapshot_that_recovered_no_game_object_is_not_reused()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority());
        var first = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);
        repository.CompletedSnapshot = repository.CompletedSnapshot! with { RecoveryStatus = SceneRecoveryStatus.StubOrUnavailable };

        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal(SceneQueryStatus.NoRecoverableSceneObjects, failure.Status);
        Assert.Contains(first.SceneSnapshotId, failure.Message, StringComparison.Ordinal);
        Assert.Contains("--force", failure.Message, StringComparison.Ordinal);
        Assert.Single(repository.CreatedSnapshots);
    }

    [Fact]
    public async Task Restored_reconstruction_passes_script_layouts_and_records_the_source()
    {
        var repository = CreateRepository(replayVerified: true);
        var extractionRoot = CreateExtractionRoot(restored: true);
        var workflow = CreateWorkflow(repository, Authority(extractionRoot));

        await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        var layouts = Assert.Single(repository.ScriptLayoutsSeen);
        Assert.NotNull(layouts);
        Assert.Equal(Path.Combine(extractionRoot, "reconstructed"), layouts.ManagedAssembliesPath);
        Assert.Contains(_extractionId, layouts.Identity, StringComparison.Ordinal);
        Assert.StartsWith($"{SceneIndexWorkflow.ScriptLayoutGenerator} over extraction {_extractionId}", repository.CompletedSnapshot!.ScriptLayoutSource);
    }

    [Fact]
    public async Task Unrestored_reconstruction_passes_no_layouts_and_says_why()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(CreateExtractionRoot(restored: false)));

        await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(repository.ScriptLayoutsSeen));
        var label = repository.CompletedSnapshot!.ScriptLayoutSource;
        Assert.StartsWith("unavailable:", label);
        Assert.Contains("cpp2il-reconstructed-assemblies-v2", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_layouts_change_the_scene_snapshot_identity()
    {
        var withoutLayouts = await RunForSnapshotIdAsync(CreateExtractionRoot(restored: false));
        var withLayouts = await RunForSnapshotIdAsync(CreateExtractionRoot(restored: true));

        Assert.NotEqual(withoutLayouts, withLayouts);
    }

    [Fact]
    public void Script_layout_identity_is_part_of_the_identity_hash()
    {
        SceneSnapshotContainerFact[] containers = [new("Schedule I_Data/level0", 10, new string('a', 64), "[]")];
        string Create(string? layouts) => SceneSnapshotIdentity.Create(
            "build", "extraction", new string('b', 64), "index", "assetstools-net", SceneIndexWorkflow.ParserVersion, 22, containers,
            classDatabaseIdentity: "db", scriptLayoutIdentity: layouts);

        Assert.NotEqual(Create(null), Create("layouts-a"));
        Assert.NotEqual(Create("layouts-a"), Create("layouts-b"));
        Assert.Equal(Create("layouts-a"), Create("layouts-a"));
    }

    private async Task<string> RunForSnapshotIdAsync(string extractionRoot)
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority(extractionRoot));
        return (await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken)).SceneSnapshotId;
    }

    // An extraction root whose reconstructed/Assembly-CSharp.dll either carries restored
    // [SerializeField] attributes (the script-layout fixture) or none (the managed fixture, like v1).
    private string CreateExtractionRoot(bool restored)
    {
        var root = Path.Combine(_root, "extractions", restored ? "restored" : "unrestored");
        var reconstructed = Path.Combine(root, "reconstructed");
        Directory.CreateDirectory(reconstructed);
        var source = restored
            ? Path.Combine(AppContext.BaseDirectory, "script-layout-fixture", "Assembly-CSharp.dll")
            : Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll");
        File.Copy(source, Path.Combine(reconstructed, "Assembly-CSharp.dll"), overwrite: true);
        return root;
    }

    [Fact]
    public async Task Promoted_scene_index_contains_a_bounded_manifest_with_counts_and_hash()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority());

        var result = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        var manifest = Path.Combine(OwnedScenePaths.ForScheduleOne(_root, _buildId, result.SceneSnapshotId).FinalRoot, "scene-index.manifest.json");
        Assert.True(File.Exists(manifest));
        var text = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken);
        Assert.Contains("sceneDataSha256", text, StringComparison.Ordinal);
        Assert.Contains("containerCount", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_completed_scene_snapshot_is_reused_and_force_creates_a_new_snapshot()
    {
        var repository = CreateRepository(replayVerified: true);
        var workflow = CreateWorkflow(repository, Authority());

        var first = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);
        var reused = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);
        var forced = await workflow.RunScheduleOneAsync(_buildId, true, TestContext.Current.CancellationToken);

        Assert.False(first.Reused);
        Assert.True(reused.Reused);
        Assert.False(forced.Reused);
        Assert.Equal(_buildId, first.BuildId);
        Assert.Equal(_codeIndexId, first.CodeIndexId);
        Assert.Equal(SceneIndexWorkflow.ParserId, first.ParserId);
        Assert.Equal(SceneIndexWorkflow.ParserVersion, first.ParserVersion);
        Assert.StartsWith(SceneIndexWorkflow.ParserVersion + ":forced:", forced.ParserVersion, StringComparison.Ordinal);
        Assert.NotEqual(first.SceneSnapshotId, forced.SceneSnapshotId);
        Assert.True(File.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, first.SceneSnapshotId).CompleteMarkerPath));
        Assert.Equal(1, first.ContainerCount);
        Assert.Equal(1, first.SceneCount);
        Assert.Equal(first.SceneCount, reused.SceneCount);
        Assert.Equal(first.GameObjectCount, reused.GameObjectCount);
        Assert.Equal(first.ComponentCount, reused.ComponentCount);
        Assert.Equal(first.ReferenceCount, reused.ReferenceCount);
        Assert.Equal(first.ContainerCount, reused.ContainerCount);
        Assert.Equal(first.TransformCount, reused.TransformCount);
        Assert.Equal(first.RecoveryCounts, reused.RecoveryCounts);
    }

    [Fact]
    public async Task Failed_deterministic_rerun_reconciles_stale_owned_staging_and_final_paths()
    {
        var repository = CreateRepository(replayVerified: true);
        var parseCalls = 0;
        var workflow = CreateWorkflow(repository, Authority(), (containers, _) =>
        {
            parseCalls++;
            if (parseCalls == 1)
                throw new InvalidDataException("synthetic crash before completion");
            return Parsed(containers);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));
        var sceneSnapshotId = Assert.Single(repository.CreatedSnapshots).SceneSnapshotId;
        var paths = OwnedScenePaths.ForScheduleOne(_root, _buildId, sceneSnapshotId);
        Directory.CreateDirectory(paths.StagingRoot);
        await File.WriteAllTextAsync(
            Path.Combine(paths.StagingRoot, "partial.tmp"),
            "partial",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.FinalRoot);
        await File.WriteAllTextAsync(
            Path.Combine(paths.FinalRoot, "orphan.tmp"),
            "orphan",
            TestContext.Current.CancellationToken);

        var result = await workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken);

        Assert.Equal(sceneSnapshotId, result.SceneSnapshotId);
        Assert.False(result.Reused);
        Assert.False(Directory.Exists(paths.StagingRoot));
        Assert.False(File.Exists(Path.Combine(paths.FinalRoot, "orphan.tmp")));
        Assert.True(File.Exists(paths.CompleteMarkerPath));
    }

    [Fact]
    public async Task Create_failure_does_not_delete_final_artifact_created_after_reuse_check()
    {
        var repository = CreateRepository(replayVerified: true);
        repository.BeforeCompletedSnapshotLookup = sceneSnapshotId =>
        {
            var paths = OwnedScenePaths.ForScheduleOne(_root, _buildId, sceneSnapshotId);
            Directory.CreateDirectory(paths.FinalRoot);
            File.WriteAllText(Path.Combine(paths.FinalRoot, "published.marker"), "published");
        };
        repository.CreateException = new InvalidOperationException("published scene snapshot is immutable");
        var workflow = CreateWorkflow(repository, Authority());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        var sceneSnapshotId = repository.LastCheckedSnapshotId!;
        var paths = OwnedScenePaths.ForScheduleOne(_root, _buildId, sceneSnapshotId);
        Assert.True(File.Exists(Path.Combine(paths.FinalRoot, "published.marker")));
    }

    [Fact]
    public async Task Concurrent_deterministic_runs_are_serialized_before_reuse_check()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository(replayVerified: true);
        repository.FirstCompletedLookupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.ReleaseFirstCompletedLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.SecondCompletedLookupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflow(repository, Authority());

        var first = Task.Run(() => workflow.RunScheduleOneAsync(_buildId, false, cancellationToken));
        await repository.FirstCompletedLookupEntered.Task;
        var second = Task.Run(() => workflow.RunScheduleOneAsync(_buildId, false, cancellationToken));

        try
        {
            var observed = await Task.WhenAny(repository.SecondCompletedLookupEntered.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
            Assert.NotSame(repository.SecondCompletedLookupEntered.Task, observed);
        }
        finally
        {
            repository.ReleaseFirstCompletedLookup.TrySetResult();
        }

        await first;
        var failure = await Assert.ThrowsAsync<SceneIndexFailureException>(() => second);
        Assert.Contains("already", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private SceneIndexWorkflow CreateWorkflow(
        WorkflowRepository repository,
        Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> authority,
        Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, IReadOnlyList<ParsedSceneContainer>>? parse = null,
        string unityVersion = "2022.3.62f1",
        UnityClassDatabaseDescriptor? classDatabase = null)
    {
        var installRoot = repository.Environment.Installation.InstallationRoot!;
        Directory.CreateDirectory(Path.Combine(installRoot, "Schedule I_Data"));
        WriteSerializedFile(Path.Combine(installRoot, "Schedule I_Data", "level0"), unityVersion);
        var parser = new DelegateParser((containers, cancellationToken) =>
        {
            repository.ParserCalls++;
            repository.LastParsedContainers = containers;
            return Task.FromResult(parse?.Invoke(containers, cancellationToken) ?? Parsed(containers));
        }, classDatabase, repository.ScriptLayoutsSeen.Add);
        var resolver = new SceneCodeSymbolResolver(
            repository,
            (_, _) => Task.FromResult<SceneCodeBuildAuthority?>(new(_extractionId, _buildId)));
        return new SceneIndexWorkflow(
            _root,
            repository,
            repository,
            repository,
            authority,
            new SceneInputVerifier(new Sha256FileHasher()),
            parser,
            new SceneNormalizer(resolver, new SceneRecoveryClassifier()));
    }

    private WorkflowRepository CreateRepository(bool replayVerified)
    {
        var installRoot = Path.Combine(_root, "game");
        var input = new InputSnapshot(
            _inputId,
            _buildId,
            Path.Combine(_root, "inputs", _inputId),
            new string('1', 64),
            DateTimeOffset.UtcNow,
            replayVerified,
            replayVerified ? DateTimeOffset.UtcNow : null,
            new InputManifest([]));
        var environment = new EnvironmentSnapshot(
            2,
            new GameBuild(_buildId, new string('2', 64), new string('3', 64), DateTimeOffset.UtcNow, true),
            new InstallationObservation(null, null, null, installRoot, null, null),
            [],
            "test",
            DateTimeOffset.UtcNow);
        var attempt = new ExtractionAttempt(
            "attempt", "recipe", _buildId, "tool", "profile", 1, "profile", "policy", 1, "policy", 1, 1,
            ExtractionInputSource.ArchivedSnapshot, _inputId, ExtractionAttemptStatus.Succeeded, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "work", "out", "err", false, false, 0, 0,
            null, 0, null, null, null, false, 0, 0, null, _extractionId);
        return new WorkflowRepository(
            input,
            attempt,
            environment,
            new IndexRunRecord(_codeIndexId, _codeSnapshotId, IndexRunStatus.Completed, "2026-08-15T00:00:00Z"),
            new CodeSnapshotRecord(_codeSnapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, _extractionId, "2026-08-15T00:00:00Z", "environment"));
    }

    private Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> Authority(string rootPath = "validated") =>
        (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(new(
            _buildId,
            new PreferredExtraction(_buildId, _extractionId, DateTimeOffset.UnixEpoch, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(_extractionId, "recipe", _buildId, "tool", "attempt", "profile", 1, "profile", 1, 1,
                new string('4', 64), rootPath, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid,
                new ExtractionStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []))));

    private static IReadOnlyList<ParsedSceneContainer> Parsed(IReadOnlyList<VerifiedSceneContainer> containers) =>
        containers.Select(container => new ParsedSceneContainer(
            container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256,
            container.UnityVersion, container.SerializedFileVersion, [], [], false)).ToArray();

    private static void WriteSerializedFile(string path, string unityVersion)
    {
        var metadata = Encoding.ASCII.GetBytes(unityVersion + "\0");
        var fileSize = 48 + metadata.Length;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        WriteBigEndian(writer, 0u); WriteBigEndian(writer, (uint)fileSize); WriteBigEndian(writer, 22u); WriteBigEndian(writer, 48u);
        writer.Write(false); writer.Write(new byte[3]);
        WriteBigEndian(writer, (uint)metadata.Length); WriteBigEndian(writer, (long)fileSize); WriteBigEndian(writer, 48L); writer.Write(new byte[8]);
        writer.Write(metadata);
    }

    private static void WriteBigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteBigEndian(BinaryWriter writer, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        writer.Write(bytes);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed class DelegateParser(
        Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, Task<IReadOnlyList<ParsedSceneContainer>>> parse,
        UnityClassDatabaseDescriptor? classDatabase = null,
        Action<SceneScriptLayoutSource?>? onScriptLayouts = null) : IUnitySerializedFileParser
    {
        public UnityClassDatabaseDescriptor? ClassDatabase => classDatabase;
        public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(IReadOnlyList<VerifiedSceneContainer> containers, CancellationToken cancellationToken) => parse(containers, cancellationToken);
        public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(IReadOnlyList<VerifiedSceneContainer> containers, SceneScriptLayoutSource? scriptLayouts, CancellationToken cancellationToken)
        {
            onScriptLayouts?.Invoke(scriptLayouts);
            return parse(containers, cancellationToken);
        }
    }

    private sealed class WorkflowRepository(
        InputSnapshot input,
        ExtractionAttempt attempt,
        EnvironmentSnapshot environment,
        IndexRunRecord indexRun,
        CodeSnapshotRecord codeSnapshot) : IIndexRepository, ISceneRepository, IExtractionRepository, IAtlasRepository
    {
        public InputSnapshot Input { get; } = input;
        public ExtractionAttempt Attempt { get; } = attempt;
        public EnvironmentSnapshot Environment { get; } = environment;
        public IndexRunRecord IndexRun { get; set; } = indexRun;
        public CodeSnapshotRecord CodeSnapshot { get; set; } = codeSnapshot;
        public int ParserCalls { get; set; }
        public List<SceneScriptLayoutSource?> ScriptLayoutsSeen { get; } = [];
        public IReadOnlyList<VerifiedSceneContainer> LastParsedContainers { get; set; } = [];
        public bool ThrowOnComplete { get; set; }
        public bool ThrowOnStart { get; set; }
        public List<SceneSnapshotRecord> CreatedSnapshots { get; } = [];
        public List<string> FailedSnapshotIds { get; } = [];
        public Dictionary<string, string> FailureCodes { get; } = new(StringComparer.Ordinal);
        public List<string> PublishedSnapshotIds { get; } = [];
        public SceneSnapshotRecord? CompletedSnapshot { get; set; }
        public SceneWriteSet? CompletedWriteSet { get; private set; }
        public string? LastCheckedSnapshotId { get; private set; }
        public Action<string>? BeforeCompletedSnapshotLookup { get; set; }
        public Exception? CreateException { get; set; }
        public TaskCompletionSource? FirstCompletedLookupEntered { get; set; }
        public TaskCompletionSource? ReleaseFirstCompletedLookup { get; set; }
        public TaskCompletionSource? SecondCompletedLookupEntered { get; set; }
        private int _completedSnapshotLookupCount;

        public Task<ExtractionAttempt?> GetAttemptAsync(string attemptId, CancellationToken cancellationToken) => Task.FromResult<ExtractionAttempt?>(Attempt);
        public Task<InputSnapshot?> GetInputSnapshotAsync(string inputSnapshotId, CancellationToken cancellationToken) => Task.FromResult<InputSnapshot?>(Input);
        public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult<EnvironmentSnapshot?>(Environment);
        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) => Task.FromResult<IndexRunRecord?>(IndexRun);
        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => Task.FromResult<CodeSnapshotRecord?>(CodeSnapshot);
        public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken)
        {
            LastCheckedSnapshotId = sceneSnapshotId;
            var lookup = Interlocked.Increment(ref _completedSnapshotLookupCount);
            if (lookup == 1 && FirstCompletedLookupEntered is not null && ReleaseFirstCompletedLookup is not null)
            {
                FirstCompletedLookupEntered.TrySetResult();
                ReleaseFirstCompletedLookup.Task.GetAwaiter().GetResult();
            }
            else if (lookup > 1 && SecondCompletedLookupEntered is not null)
            {
                SecondCompletedLookupEntered.TrySetResult();
            }
            BeforeCompletedSnapshotLookup?.Invoke(sceneSnapshotId);
            return Task.FromResult(PublishedSnapshotIds.Contains(sceneSnapshotId) && CompletedSnapshot?.SceneSnapshotId == sceneSnapshotId ? CompletedSnapshot : null);
        }
        public Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            if (CreateException is not null)
                throw CreateException;
            CreatedSnapshots.Add(snapshot);
            return Task.CompletedTask;
        }
        public Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken)
        {
            if (ThrowOnStart) throw new InvalidOperationException("injected start failure");
            return Task.CompletedTask;
        }
        public Task CompleteSceneSnapshotAsync(string sceneSnapshotId, SceneWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken)
        {
            if (ThrowOnComplete) throw new InvalidOperationException("injected database rollback");
            CompletedSnapshot = writeSet.Snapshot with { Status = SceneSnapshotStatus.Completed, CompletedAtUtc = completedAtUtc };
            CompletedWriteSet = writeSet;
            return Task.CompletedTask;
        }
        public Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) { FailedSnapshotIds.Add(sceneSnapshotId); FailureCodes[sceneSnapshotId] = failureCode; return Task.CompletedTask; }
        public Task PublishSceneSnapshotAsync(string sceneSnapshotId, string publishedAtUtc, CancellationToken cancellationToken) { PublishedSnapshotIds.Add(sceneSnapshotId); return Task.CompletedTask; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveSnapshotAsync(EnvironmentSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GameBuild?> GetBuildAsync(string buildId, CancellationToken cancellationToken) => Task.FromResult<GameBuild?>(Environment.Build);
        public Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateAttemptAsync(ExtractionAttempt item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TransitionAttemptAsync(ExtractionAttempt item, ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveInputSnapshotAsync(InputSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkInputSnapshotReplayVerifiedAsync(string inputSnapshotId, string expectedBuildId, string expectedManifestDigest, DateTimeOffset verifiedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IndexRunRecord?>(IndexRun);
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSymbolRecord>>([]);
        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken) => Task.FromResult(PublishedSnapshotIds.Contains(CompletedSnapshot?.SceneSnapshotId ?? string.Empty) ? CompletedSnapshot : null);
        public Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(string sceneSnapshotId, CancellationToken cancellationToken)
        {
            if (CompletedWriteSet is null || !PublishedSnapshotIds.Contains(sceneSnapshotId))
                return Task.FromResult<SceneIndexStatistics?>(null);
            return Task.FromResult<SceneIndexStatistics?>(new(
                CompletedWriteSet.Containers.Count,
                CompletedWriteSet.Documents.Count,
                CompletedWriteSet.GameObjects.Count,
                CompletedWriteSet.Transforms.Count,
                CompletedWriteSet.Components.Count,
                CompletedWriteSet.References.Count,
                CompletedWriteSet.Documents.Select(row => row.RecoveryStatus)
                    .Concat(CompletedWriteSet.GameObjects.Select(row => row.RecoveryStatus))
                    .Concat(CompletedWriteSet.Transforms.Select(row => row.RecoveryStatus))
                    .Concat(CompletedWriteSet.Components.Select(row => row.RecoveryStatus))
                    .Concat(CompletedWriteSet.References.Select(row => row.RecoveryStatus))
                    .GroupBy(status => status.ToString(), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)));
        }
        public Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneContainerRecord>>([]);
        public Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneDocumentRecord>(CompletedWriteSet?.Documents.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneGameObjectRecord>(CompletedWriteSet?.GameObjects.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneTransformRecord?> GetTransformAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneComponentRecord>(CompletedWriteSet?.Components.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneReferenceRecord>(CompletedWriteSet?.References.Count ?? 0, 0, []));
        public Task<SceneScriptFieldSetRecord?> GetScriptFieldSetAsync(string sceneSnapshotId, string ownerId, CancellationToken cancellationToken) => Task.FromResult(CompletedWriteSet?.ScriptFieldSets.SingleOrDefault(row => row.OwnerId == ownerId));
        public Task<SceneScriptableAssetRecord?> GetScriptableAssetAsync(string sceneSnapshotId, string assetId, CancellationToken cancellationToken) => Task.FromResult(CompletedWriteSet?.ScriptableAssets.SingleOrDefault(row => row.AssetId == assetId));
        public Task<IReadOnlyList<SceneScriptableAssetRecord>> FindScriptableAssetsAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneScriptableAssetRecord>>([]);
        public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
