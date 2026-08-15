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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal("NoPreferredVerifiedExtraction", exception.Message);
        Assert.Empty(repository.CreatedSnapshots);
    }

    [Fact]
    public async Task Replay_unverified_extraction_input_is_rejected_before_parsing()
    {
        var repository = CreateRepository(replayVerified: false);
        var workflow = CreateWorkflow(repository, Authority());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal("NoReplayVerifiedExtractionInput", exception.Message);
        Assert.Equal(0, repository.ParserCalls);
    }

    [Fact]
    public async Task Cross_build_code_index_is_rejected_before_a_scene_snapshot_starts()
    {
        var repository = CreateRepository(replayVerified: true);
        repository.CodeSnapshot = repository.CodeSnapshot with { SourceIdentity = new string('f', 64) };
        var workflow = CreateWorkflow(repository, Authority());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal("CrossBuildCodeIndex", exception.Message);
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal("PreferredExtractionChanged", exception.Message);
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunScheduleOneAsync(_buildId, false, TestContext.Current.CancellationToken));

        Assert.Equal("CodeIndexChanged", exception.Message);
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
        Assert.NotEqual(first.SceneSnapshotId, forced.SceneSnapshotId);
        Assert.True(File.Exists(OwnedScenePaths.ForScheduleOne(_root, _buildId, first.SceneSnapshotId).CompleteMarkerPath));
        Assert.Equal(1, first.ContainerCount);
        Assert.Equal(1, first.SceneCount);
        Assert.Equal(first.SceneCount, reused.SceneCount);
        Assert.Equal(first.GameObjectCount, reused.GameObjectCount);
        Assert.Equal(first.ComponentCount, reused.ComponentCount);
        Assert.Equal(first.ReferenceCount, reused.ReferenceCount);
    }

    private SceneIndexWorkflow CreateWorkflow(
        WorkflowRepository repository,
        Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> authority,
        Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, IReadOnlyList<ParsedSceneContainer>>? parse = null,
        string unityVersion = "2022.3.62f1")
    {
        var installRoot = repository.Environment.Installation.InstallationRoot!;
        Directory.CreateDirectory(Path.Combine(installRoot, "Schedule I_Data"));
        WriteSerializedFile(Path.Combine(installRoot, "Schedule I_Data", "level0"), unityVersion);
        var parser = new DelegateParser((containers, cancellationToken) =>
        {
            repository.ParserCalls++;
            repository.LastParsedContainers = containers;
            return Task.FromResult(parse?.Invoke(containers, cancellationToken) ?? Parsed(containers));
        });
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

    private Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> Authority() =>
        (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(new(
            _buildId,
            new PreferredExtraction(_buildId, _extractionId, DateTimeOffset.UnixEpoch, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(_extractionId, "recipe", _buildId, "tool", "attempt", "profile", 1, "profile", 1, 1,
                new string('4', 64), "validated", DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid,
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

    private sealed class DelegateParser(Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, Task<IReadOnlyList<ParsedSceneContainer>>> parse) : IUnitySerializedFileParser
    {
        public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(IReadOnlyList<VerifiedSceneContainer> containers, CancellationToken cancellationToken) => parse(containers, cancellationToken);
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
        public IReadOnlyList<VerifiedSceneContainer> LastParsedContainers { get; set; } = [];
        public bool ThrowOnComplete { get; set; }
        public bool ThrowOnStart { get; set; }
        public List<SceneSnapshotRecord> CreatedSnapshots { get; } = [];
        public List<string> FailedSnapshotIds { get; } = [];
        public List<string> PublishedSnapshotIds { get; } = [];
        public SceneSnapshotRecord? CompletedSnapshot { get; private set; }
        public SceneWriteSet? CompletedWriteSet { get; private set; }

        public Task<ExtractionAttempt?> GetAttemptAsync(string attemptId, CancellationToken cancellationToken) => Task.FromResult<ExtractionAttempt?>(Attempt);
        public Task<InputSnapshot?> GetInputSnapshotAsync(string inputSnapshotId, CancellationToken cancellationToken) => Task.FromResult<InputSnapshot?>(Input);
        public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult<EnvironmentSnapshot?>(Environment);
        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) => Task.FromResult<IndexRunRecord?>(IndexRun);
        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => Task.FromResult<CodeSnapshotRecord?>(CodeSnapshot);
        public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken) => Task.FromResult(PublishedSnapshotIds.Contains(sceneSnapshotId) && CompletedSnapshot?.SceneSnapshotId == sceneSnapshotId ? CompletedSnapshot : null);
        public Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken) { CreatedSnapshots.Add(snapshot); return Task.CompletedTask; }
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
        public Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) { FailedSnapshotIds.Add(sceneSnapshotId); return Task.CompletedTask; }
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
        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken) => Task.FromResult(PublishedSnapshotIds.Contains(CompletedSnapshot?.SceneSnapshotId ?? string.Empty) ? CompletedSnapshot : null);
        public Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneContainerRecord>>([]);
        public Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneDocumentRecord>(CompletedWriteSet?.Documents.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneGameObjectRecord>(CompletedWriteSet?.GameObjects.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneComponentRecord>(CompletedWriteSet?.Components.Count ?? 0, 0, []));
        public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactKindAsync(string sceneSnapshotId, string kind, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneReferenceRecord>(CompletedWriteSet?.References.Count ?? 0, 0, []));
        public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
