using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Scene;

public sealed class SceneOutputIsolationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly string _buildId = new('a', 64);
    private readonly string _extractionId = new('b', 64);
    private readonly string _inputId = new('c', 64);
    private readonly string _indexId = new('d', 64);
    private readonly string _codeSnapshotId = new('e', 64);

    [Fact]
    public async Task RunScheduleOneAsync_writes_only_below_the_Atlas_data_root_and_never_copies_the_game_install()
    {
        var repositoryRoot = Path.Combine(_root, "repository");
        var dataRoot = Path.Combine(_root, "atlas-data");
        var installRoot = Path.Combine(_root, "game-install");
        var installFile = Path.Combine(installRoot, "Schedule I_Data", "level0");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
        WriteSerializedFile(installFile);
        var installBytes = await File.ReadAllBytesAsync(installFile, TestContext.Current.CancellationToken);
        var installWriteTime = File.GetLastWriteTimeUtc(installFile);
        var repository = new SqliteAtlasRepository(Path.Combine(dataRoot, "atlas.db"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var environment = Environment(installRoot);
        await repository.SaveSnapshotAsync(environment, TestContext.Current.CancellationToken);
        await SeedWorkflowAuthorityAsync(dataRoot, EnvironmentSnapshotId.Create(environment), TestContext.Current.CancellationToken);
        string? observedStagingRoot = null;
        var workflow = new SceneIndexWorkflow(
            dataRoot,
            repository,
            repository,
            repository,
            (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(Authority()),
            new SceneInputVerifier(new Sha256FileHasher()),
            new DelegateParser((containers, _) =>
            {
                observedStagingRoot = Assert.Single(Directory.GetDirectories(dataRoot, "*.staging", SearchOption.AllDirectories));
                return Task.FromResult<IReadOnlyList<ParsedSceneContainer>>(containers.Select(ToParsed).ToArray());
            }),
            new SceneNormalizer(new SceneCodeSymbolResolver(repository), new SceneRecoveryClassifier()));

        var result = await workflow.RunScheduleOneAsync(_buildId, force: false, TestContext.Current.CancellationToken);

        var finalRoot = Path.Combine(dataRoot, "builds", _buildId, "scene-indexes", result.SceneSnapshotId);
        Assert.NotNull(observedStagingRoot);
        Assert.True(IsDescendantOf(dataRoot, observedStagingRoot!));
        Assert.False(Directory.Exists(observedStagingRoot!));
        Assert.True(File.Exists(Path.Combine(finalRoot, "scene-index.manifest.json")));
        Assert.True(File.Exists(Path.Combine(finalRoot, "complete.marker")));
        Assert.All(Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories), path => Assert.True(IsDescendantOf(dataRoot, path)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(repositoryRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(installBytes, await File.ReadAllBytesAsync(installFile, TestContext.Current.CancellationToken));
        Assert.Equal(installWriteTime, File.GetLastWriteTimeUtc(installFile));
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private EnvironmentSnapshot Environment(string installRoot) => new(2, new GameBuild(_buildId, new string('1', 64), new string('2', 64), DateTimeOffset.UtcNow, true), new InstallationObservation(null, null, null, installRoot, null, null), [], "test", DateTimeOffset.UtcNow);

    private PreferredVerifiedExtraction Authority() => new(_buildId, new PreferredExtraction(_buildId, _extractionId, DateTimeOffset.UnixEpoch, ExtractionPreferenceReason.ManualPromotion), new ValidatedExtraction(_extractionId, "recipe", _buildId, "tool", "attempt", "profile", 1, "profile", 1, 1, new string('3', 64), "root", DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])));

    private async Task SeedWorkflowAuthorityAsync(string dataRoot, string environmentSnapshotId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataRoot, "atlas.db")}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = OFF;
            INSERT INTO input_snapshots(input_snapshot_id, build_id, root_path, manifest_digest, created_at_utc, replay_verified, replay_verified_at_utc) VALUES ('{_inputId}', '{_buildId}', 'input-root', '{new string('4', 64)}', '2026-08-15T00:00:00.0000000+00:00', 1, '2026-08-15T00:00:00.0000000+00:00');
            INSERT INTO tool_instances(tool_instance_id, tool_name, platform, trust_level, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ('tool', 'tool', 'win', 'ManagedPinned', '{new string('5', 64)}', 'path', '2026-08-15T00:00:00.0000000+00:00', '2026-08-15T00:00:00.0000000+00:00', 'Verified');
            INSERT INTO extraction_attempts(attempt_id, build_id, profile_id, profile_version, profile_digest, validation_policy_id, validation_policy_version, validation_policy_digest, adapter_version, extraction_schema_version, input_snapshot_id, status, created_at_utc, working_path, stdout_path, stderr_path, stdout_truncated, stderr_truncated, stdout_discarded_bytes, stderr_discarded_bytes, keep_failed_artifacts, discarded_file_count, discarded_byte_count) VALUES ('attempt', '{_buildId}', 'profile', 1, 'digest', 'policy', 1, 'digest', 1, 1, '{_inputId}', 'Succeeded', '2026-08-15T00:00:00.0000000+00:00', 'work', 'stdout', 'stderr', 0, 0, 0, 0, 0, 0, 0);
            INSERT INTO validated_extractions(extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id, profile_id, profile_version, profile_digest, adapter_version, extraction_schema_version, artifact_manifest_digest, root_path, created_at_utc, trust_level, validation_outcome, artifact_count, library_count, managed_assembly_count, type_count, method_count, field_count, property_count, event_count, total_output_bytes, total_managed_bytes) VALUES ('{_extractionId}', 'recipe', '{_buildId}', 'tool', 'attempt', 'profile', 1, 'digest', 1, 1, '{new string('3', 64)}', 'root', '2026-08-15T00:00:00.0000000+00:00', 'ManagedPinned', 'Valid', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('{_codeSnapshotId}', 'ScheduleI', 'Installed', '{environmentSnapshotId}', '{_extractionId}', '2026-08-15T00:00:00.0000000+00:00');
            INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('{_indexId}', '{_codeSnapshotId}', 'Completed', '2026-08-15T00:00:00.0000000+00:00', '2026-08-15T00:00:00.0000000+00:00');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ParsedSceneContainer ToParsed(VerifiedSceneContainer container) => new(container.RelativePath, container.PrimaryPath, container.SidecarPaths, container.Sha256, container.UnityVersion, container.SerializedFileVersion, [], [], false);
    private static bool IsDescendantOf(string root, string candidate) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void WriteSerializedFile(string path)
    {
        var metadata = Encoding.ASCII.GetBytes("2022.3.62f1\0"); var fileSize = 48 + metadata.Length;
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8, leaveOpen: false);
        WriteBigEndian(writer, 0u); WriteBigEndian(writer, (uint)fileSize); WriteBigEndian(writer, 22u); WriteBigEndian(writer, 48u); writer.Write(false); writer.Write(new byte[3]); WriteBigEndian(writer, (uint)metadata.Length); WriteBigEndian(writer, (long)fileSize); WriteBigEndian(writer, 48L); writer.Write(new byte[8]); writer.Write(metadata);
    }

    private static void WriteBigEndian(BinaryWriter writer, uint value) { Span<byte> bytes = stackalloc byte[sizeof(uint)]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); writer.Write(bytes); }
    private static void WriteBigEndian(BinaryWriter writer, long value) { Span<byte> bytes = stackalloc byte[sizeof(long)]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); writer.Write(bytes); }

    private sealed class DelegateParser(Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, Task<IReadOnlyList<ParsedSceneContainer>>> parse) : IUnitySerializedFileParser
    {
        public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(IReadOnlyList<VerifiedSceneContainer> containers, CancellationToken cancellationToken) => parse(containers, cancellationToken);
    }
}
