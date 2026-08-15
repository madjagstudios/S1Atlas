using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Scene;

public sealed class SceneWorkflowIntegrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-scene-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _buildId = new('a', 64);
    private readonly string _extractionId = new('b', 64);
    private readonly string _inputId = new('c', 64);
    private readonly string _indexId = new('d', 64);
    private readonly string _codeSnapshotId = new('e', 64);
    private readonly string _symbolId = new('1', 64);
    private IntegrationSerializedFileFixture? _fixture;

    [Fact]
    public async Task Offline_workflow_uses_verified_binary_facts_and_publishes_bounded_queryable_json()
    {
        var setup = await CreateSetupAsync();
        var hashesBefore = HashFiles(_fixture!.SerializedFilePaths);

        var result = await setup.Workflow.RunScheduleOneAsync(
            _buildId,
            force: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Reused);
        Assert.Equal(hashesBefore, HashFiles(_fixture.SerializedFilePaths));
        Assert.Equal(1, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 8 AND name = 'scene-intelligence-v8';"));

        var paths = OwnedScenePaths.ForScheduleOne(setup.DataRoot, _buildId, result.SceneSnapshotId);
        Assert.True(File.Exists(paths.CompleteMarkerPath));
        Assert.True(File.Exists(Path.Combine(paths.FinalRoot, "scene-index.manifest.json")));
        Assert.False(Directory.Exists(paths.StagingRoot));

        var scenes = await setup.Repository.ListScenesAsync(
            new SceneListQueryOptions(result.SceneSnapshotId, Limit: 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, scenes.TotalCount);
        Assert.Equal(1, scenes.ReturnedCount);

        var components = await setup.Repository.ListComponentsAsync(
            new ComponentListQueryOptions(result.SceneSnapshotId, Limit: 50),
            TestContext.Current.CancellationToken);
        Assert.Equal(8, components.TotalCount);
        var behaviours = components.Rows
            .Where(component => component.Kind == "MonoBehaviour")
            .ToArray();
        Assert.Equal(2, behaviours.Length);
        Assert.All(behaviours, component =>
        {
            Assert.Equal(SceneRecoveryStatus.GraphOnly, component.RecoveryStatus);
            Assert.Equal(SceneResolutionStatus.Resolved, component.TypeResolutionStatus);
            Assert.Equal(_symbolId, component.ResolvedTypeSymbolId);
            Assert.Equal(_indexId, component.ResolvedCodeIndexId);
        });

        var references = await setup.Repository.ListReferencesAsync(
            new ReferenceListQueryOptions(result.SceneSnapshotId, Limit: 100),
            TestContext.Current.CancellationToken);
        Assert.Contains(references.Rows, reference =>
            reference.FieldPath == "m_LocalTarget" &&
            reference.ResolutionStatus == SceneResolutionStatus.Resolved &&
            reference.TargetGameObjectId is not null);
        Assert.Contains(references.Rows, reference =>
            reference.FieldPath == "m_ExternalTarget" &&
            reference.ResolutionStatus == SceneResolutionStatus.Resolved &&
            reference.TargetGameObjectId is not null);
        Assert.Equal(2, references.Rows.Count(reference =>
            reference.FieldPath == "m_MissingTarget" &&
            reference.ResolutionStatus == SceneResolutionStatus.UnresolvedText &&
            reference.TargetText is not null));
        Assert.Contains(references.Rows, reference =>
            reference.FieldPath == "m_Script" &&
            reference.ResolutionStatus == SceneResolutionStatus.Resolved &&
            reference.TargetSymbolId == _symbolId);

        Assert.Equal(0, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'serialized_fields';"));

        var network = new RejectingNetworkHandler();
        var processExtractor = new RejectingProcessExtractor();
        var application = new CliApplication(
            setup.DataRoot,
            "0.1.0-test",
            Path.Combine(_root, "configuration"),
            () => new HttpClient(network, disposeHandler: false),
            processExtractorFactory: () => processExtractor);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            ["scenes", "--snapshot", result.SceneSnapshotId, "--limit", "1", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.True(exitCode == 0, output.ToString() + error);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("returnedCount").GetInt32());
        Assert.Equal(0, network.RequestCount);
        Assert.Equal(0, processExtractor.CallCount);
    }

    [Fact]
    public async Task Absent_reviewed_custom_schema_stays_graph_only_without_invented_fields_or_payloads()
    {
        const string opaqueCustomValue = "opaque-unreviewed-custom-value";
        var setup = await CreateSetupWithoutReviewedCustomSchemaAsync();

        var result = await setup.Workflow.RunScheduleOneAsync(
            _buildId,
            force: false,
            TestContext.Current.CancellationToken);

        var components = await setup.Repository.ListComponentsAsync(
            new ComponentListQueryOptions(result.SceneSnapshotId, Limit: 50),
            TestContext.Current.CancellationToken);
        var behaviours = components.Rows
            .Where(component => component.Kind == "MonoBehaviour")
            .ToArray();
        Assert.Equal(2, behaviours.Length);
        Assert.All(behaviours, component =>
        {
            Assert.Equal(SceneRecoveryStatus.GraphOnly, component.RecoveryStatus);
            Assert.Equal(SceneResolutionStatus.Resolved, component.TypeResolutionStatus);
            Assert.Equal(_symbolId, component.ResolvedTypeSymbolId);
        });

        var references = await setup.Repository.ListReferencesAsync(
            new ReferenceListQueryOptions(result.SceneSnapshotId, Limit: 100),
            TestContext.Current.CancellationToken);
        var behaviourIds = behaviours
            .Select(component => component.ComponentId)
            .ToHashSet(StringComparer.Ordinal);
        var behaviourReferences = references.Rows
            .Where(reference => reference.SourceComponentId is not null &&
                behaviourIds.Contains(reference.SourceComponentId))
            .ToArray();
        Assert.Equal(2, behaviourReferences.Count(reference => reference.FieldPath == "m_GameObject"));
        Assert.Equal(2, behaviourReferences.Count(reference => reference.FieldPath == "m_Script"));
        Assert.All(behaviours, component =>
        {
            Assert.Contains(behaviourReferences, reference =>
                reference.SourceComponentId == component.ComponentId &&
                reference.FieldPath == "m_GameObject" &&
                reference.TargetGameObjectId == component.GameObjectId &&
                reference.ResolutionStatus == SceneResolutionStatus.Resolved);
            Assert.Contains(behaviourReferences, reference =>
                reference.SourceComponentId == component.ComponentId &&
                reference.FieldPath == "m_Script" &&
                reference.TargetSymbolId == _symbolId &&
                reference.ResolutionStatus == SceneResolutionStatus.Resolved);
        });
        Assert.DoesNotContain(behaviourReferences, reference =>
            reference.FieldPath is "m_LocalTarget" or "m_ExternalTarget" or "m_MissingTarget");

        Assert.Equal(0, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'serialized_fields';"));
        Assert.Equal(0, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM pragma_table_info('components') WHERE lower(name) IN ('field', 'value', 'payload', 'blob');"));
        var databaseText = Encoding.UTF8.GetString(ReadSharedBytes(setup.DatabasePath));
        Assert.DoesNotContain(opaqueCustomValue, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("m_LocalTarget", databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("m_ExternalTarget", databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("m_MissingTarget", databaseText, StringComparison.Ordinal);

        var network = new RejectingNetworkHandler();
        var processExtractor = new RejectingProcessExtractor();
        var application = new CliApplication(
            setup.DataRoot,
            "0.1.0-test",
            Path.Combine(_root, "configuration"),
            () => new HttpClient(network, disposeHandler: false),
            processExtractorFactory: () => processExtractor);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            ["component", behaviours[0].ComponentId, "--refs", "--code", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.True(exitCode == 0, output.ToString() + error);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            (int)SceneRecoveryStatus.GraphOnly,
            json.RootElement
                .GetProperty("data")
                .GetProperty("component")
                .GetProperty("recoveryStatus")
                .GetInt32());
        Assert.DoesNotContain(opaqueCustomValue, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("m_LocalTarget", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("m_ExternalTarget", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("m_MissingTarget", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, network.RequestCount);
        Assert.Equal(0, processExtractor.CallCount);
    }

    [Fact]
    public async Task Stripped_mono_behaviour_record_publishes_as_an_attached_graph_only_component()
    {
        var setup = await CreateSetupAsync();
        var parser = new DelegateParser((containers, _) => Task.FromResult<IReadOnlyList<ParsedSceneContainer>>(
            containers.Select(container => new ParsedSceneContainer(
                container.RelativePath,
                container.PrimaryPath,
                container.SidecarPaths,
                container.Sha256,
                container.UnityVersion,
                container.SerializedFileVersion,
                Path.GetFileName(container.RelativePath) == "level0"
                    ?
                    [
                        new ParsedSceneObject(1, 1, 128, 64, ParsedSceneObjectKind.GameObject, [], new ParsedGameObjectData("Retained owner", 0, 0, true, [new ParsedScenePPtr(0, 10)]), null, null, null, null),
                        new ParsedSceneObject(10, 114, 192, 32, ParsedSceneObjectKind.MonoBehaviour, [], null, null, null, null, null)
                    ]
                    : [],
                [],
                false)).ToArray()));
        var workflow = new SceneIndexWorkflow(
            setup.DataRoot,
            setup.Repository,
            setup.Repository,
            setup.Repository,
            (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(Authority()),
            new SceneInputVerifier(new Sha256FileHasher()),
            parser,
            new SceneNormalizer(new SceneCodeSymbolResolver(setup.Repository), new SceneRecoveryClassifier()));

        var result = await workflow.RunScheduleOneAsync(
            _buildId,
            force: false,
            TestContext.Current.CancellationToken);
        var components = await setup.Repository.ListComponentsAsync(
            new ComponentListQueryOptions(result.SceneSnapshotId, Limit: 50),
            TestContext.Current.CancellationToken);

        var component = Assert.Single(components.Rows);
        Assert.Equal("MonoBehaviour", component.Kind);
        Assert.Equal(SceneRecoveryStatus.GraphOnly, component.RecoveryStatus);
        Assert.Equal(SceneResolutionStatus.Unavailable, component.TypeResolutionStatus);
        Assert.Null(component.ScriptAssembly);
        Assert.Null(component.ScriptNamespace);
        Assert.Null(component.ScriptClass);
        Assert.Null(component.ResolvedTypeSymbolId);
        Assert.NotNull(await setup.Repository.GetCompletedSceneSnapshotAsync(result.SceneSnapshotId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migration_8_completion_rolls_back_all_graph_rows_when_a_late_insert_fails()
    {
        var setup = await CreateSetupAsync();
        var verifier = new SceneInputVerifier(new Sha256FileHasher());
        var verified = await verifier.CaptureAsync(
            setup.InstallRoot,
            [
                new SceneContainerDeclaration("Schedule I_Data/level0", []),
                new SceneContainerDeclaration("Schedule I_Data/sharedassets0.assets", [])
            ],
            TestContext.Current.CancellationToken);
        var parser = new AssetsToolsUnitySerializedFileParser();
        var parsed = await parser.ParseAsync(
            verified.Containers,
            TestContext.Current.CancellationToken);
        var snapshot = new SceneSnapshotRecord(
            new('f', 64),
            _buildId,
            _extractionId,
            _inputId,
            _codeSnapshotId,
            _indexId,
            SceneIndexWorkflow.ParserId,
            SceneIndexWorkflow.ParserVersion,
            verified.ManifestDigest,
            SceneSnapshotStatus.Running,
            SceneRecoveryStatus.Unknown,
            "2026-08-15T00:00:00.0000000+00:00");
        var normalizer = new SceneNormalizer(
            new SceneCodeSymbolResolver(setup.Repository),
            new SceneRecoveryClassifier());
        var writeSet = await normalizer.NormalizeAsync(
            snapshot,
            verified.Containers,
            parsed,
            TestContext.Current.CancellationToken);
        var duplicateComponentWriteSet = writeSet with
        {
            Components = [.. writeSet.Components, writeSet.Components[0]]
        };
        await setup.Repository.CreateSceneSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        await setup.Repository.StartSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            snapshot.StartedAtUtc,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() =>
            setup.Repository.CompleteSceneSnapshotAsync(
                snapshot.SceneSnapshotId,
                duplicateComponentWriteSet,
            "2026-08-15T00:01:00.0000000+00:00",
                TestContext.Current.CancellationToken));

        Assert.Equal(0, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM scene_containers WHERE scene_snapshot_id = $snapshot;",
            ("$snapshot", snapshot.SceneSnapshotId)));
        Assert.Equal(0, await ScalarAsync<long>(
            setup.DatabasePath,
            "SELECT COUNT(*) FROM scenes WHERE scene_snapshot_id = $snapshot;",
            ("$snapshot", snapshot.SceneSnapshotId)));
        Assert.Equal("Running", await ScalarAsync<string>(
            setup.DatabasePath,
            "SELECT status FROM scene_snapshots WHERE scene_snapshot_id = $snapshot;",
            ("$snapshot", snapshot.SceneSnapshotId)));
        Assert.Null(await setup.Repository.GetCompletedSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            TestContext.Current.CancellationToken));
    }

    private Task<Setup> CreateSetupAsync() =>
        CreateSetupAsync(IntegrationSerializedFileFixture.Create);

    private Task<Setup> CreateSetupWithoutReviewedCustomSchemaAsync() =>
        CreateSetupAsync(IntegrationSerializedFileFixture.CreateWithoutReviewedCustomSchema);

    private async Task<Setup> CreateSetupAsync(
        Func<string, IntegrationSerializedFileFixture> createFixture)
    {
        var installRoot = Path.Combine(_root, "install");
        var dataRoot = Path.Combine(_root, "atlas-data");
        var databasePath = Path.Combine(dataRoot, "atlas.db");
        _fixture = createFixture(installRoot);
        var repository = new SqliteAtlasRepository(databasePath);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var environment = new EnvironmentSnapshot(
            2,
            new GameBuild(
                _buildId,
                new string('2', 64),
                new string('3', 64),
                DateTimeOffset.UtcNow,
                true),
            new InstallationObservation(
                "2022.3.62f1",
                "fixture-app",
                "fixture-build",
                installRoot,
                null,
                null),
            [],
            "0.1.0-test",
            DateTimeOffset.UtcNow);
        await repository.SaveSnapshotAsync(environment, TestContext.Current.CancellationToken);
        await SeedAuthoritiesAsync(
            databasePath,
            EnvironmentSnapshotId.Create(environment),
            TestContext.Current.CancellationToken);
        var workflow = new SceneIndexWorkflow(
            dataRoot,
            repository,
            repository,
            repository,
            (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(Authority()),
            new SceneInputVerifier(new Sha256FileHasher()),
            new AssetsToolsUnitySerializedFileParser(),
            new SceneNormalizer(
                new SceneCodeSymbolResolver(repository),
                new SceneRecoveryClassifier()));
        return new Setup(dataRoot, installRoot, databasePath, repository, workflow);
    }

    private PreferredVerifiedExtraction Authority() => new(
        _buildId,
        new PreferredExtraction(
            _buildId,
            _extractionId,
            DateTimeOffset.UnixEpoch,
            ExtractionPreferenceReason.ManualPromotion),
        new ValidatedExtraction(
            _extractionId,
            "recipe",
            _buildId,
            "tool",
            "attempt",
            "profile",
            1,
            "profile",
            1,
            1,
            new string('4', 64),
            "validated-root",
            DateTimeOffset.UtcNow,
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            new ExtractionStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])));

    private async Task SeedAuthoritiesAsync(
        string databasePath,
        string environmentSnapshotId,
        CancellationToken cancellationToken)
    {
        var canonicalName = "Fixture.Namespace.SceneGraphBehaviour";
        var canonicalKey = SymbolIdentity.Create(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            SymbolKind.Type,
            canonicalName).CanonicalKey;
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = OFF;
            INSERT INTO input_snapshots(input_snapshot_id, build_id, root_path, manifest_digest, created_at_utc, replay_verified, replay_verified_at_utc) VALUES ('{_inputId}', '{_buildId}', 'input-root', '{new string('5', 64)}', '2026-08-15T00:00:00.0000000+00:00', 1, '2026-08-15T00:00:00.0000000+00:00');
            INSERT INTO tool_instances(tool_instance_id, tool_name, platform, trust_level, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ('tool', 'fixture', 'win-x64', 'ManagedPinned', '{new string('6', 64)}', 'fixture-tool', '2026-08-15T00:00:00.0000000+00:00', '2026-08-15T00:00:00.0000000+00:00', 'Verified');
            INSERT INTO extraction_attempts(attempt_id, build_id, profile_id, profile_version, profile_digest, validation_policy_id, validation_policy_version, validation_policy_digest, adapter_version, extraction_schema_version, input_snapshot_id, status, created_at_utc, working_path, stdout_path, stderr_path, stdout_truncated, stderr_truncated, stdout_discarded_bytes, stderr_discarded_bytes, keep_failed_artifacts, discarded_file_count, discarded_byte_count) VALUES ('attempt', '{_buildId}', 'profile', 1, 'digest', 'policy', 1, 'digest', 1, 1, '{_inputId}', 'Succeeded', '2026-08-15T00:00:00.0000000+00:00', 'work', 'stdout', 'stderr', 0, 0, 0, 0, 0, 0, 0);
            INSERT INTO validated_extractions(extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id, profile_id, profile_version, profile_digest, adapter_version, extraction_schema_version, artifact_manifest_digest, root_path, created_at_utc, trust_level, validation_outcome, artifact_count, library_count, managed_assembly_count, type_count, method_count, field_count, property_count, event_count, total_output_bytes, total_managed_bytes) VALUES ('{_extractionId}', 'recipe', '{_buildId}', 'tool', 'attempt', 'profile', 1, 'digest', 1, 1, '{new string('4', 64)}', 'validated-root', '2026-08-15T00:00:00.0000000+00:00', 'ManagedPinned', 'Valid', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('{_codeSnapshotId}', 'ScheduleI', 'Installed', '{environmentSnapshotId}', '{_extractionId}', '2026-08-15T00:00:00.0000000+00:00');
            INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('{_indexId}', '{_codeSnapshotId}', 'Completed', '2026-08-15T00:00:00.0000000+00:00', '2026-08-15T00:00:01.0000000+00:00');
            INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort) VALUES ('{_symbolId}', '{_codeSnapshotId}', '{canonicalKey}', 'Type', '{canonicalName}', '{canonicalName}', 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> HashFiles(IReadOnlyList<string> paths) =>
        paths.ToDictionary(
            path => path,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            StringComparer.Ordinal);

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task<T> ScalarAsync<T>(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ??
            throw new InvalidOperationException("The scalar integration query returned null.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    public ValueTask DisposeAsync()
    {
        _fixture?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record Setup(
        string DataRoot,
        string InstallRoot,
        string DatabasePath,
        SqliteAtlasRepository Repository,
        SceneIndexWorkflow Workflow);

    private sealed class DelegateParser(
        Func<IReadOnlyList<VerifiedSceneContainer>, CancellationToken, Task<IReadOnlyList<ParsedSceneContainer>>> parse)
        : IUnitySerializedFileParser
    {
        public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
            IReadOnlyList<VerifiedSceneContainer> containers,
            CancellationToken cancellationToken) => parse(containers, cancellationToken);
    }

    private sealed class IntegrationSerializedFileFixture : IDisposable
    {
        private const string UnityVersion = "2022.3.62f1";
        private const int SerializedFileVersion = 22;

        private IntegrationSerializedFileFixture(
            string installRoot,
            IReadOnlyList<string> serializedFilePaths)
        {
            InstallRoot = installRoot;
            SerializedFilePaths = serializedFilePaths;
        }

        public string InstallRoot { get; }
        public IReadOnlyList<string> SerializedFilePaths { get; }

        public static IntegrationSerializedFileFixture Create(string installRoot)
            => Create(installRoot, includeReviewedCustomSchema: true);

        public static IntegrationSerializedFileFixture CreateWithoutReviewedCustomSchema(
            string installRoot)
            => Create(installRoot, includeReviewedCustomSchema: false);

        private static IntegrationSerializedFileFixture Create(
            string installRoot,
            bool includeReviewedCustomSchema)
        {
            var dataRoot = Path.Combine(installRoot, "Schedule I_Data");
            Directory.CreateDirectory(dataRoot);
            var bytes = CreateBytes(includeReviewedCustomSchema);
            var paths = new[]
            {
                Path.Combine(dataRoot, "level0"),
                Path.Combine(dataRoot, "sharedassets0.assets")
            };
            foreach (var path in paths)
                File.WriteAllBytes(path, bytes);
            return new IntegrationSerializedFileFixture(installRoot, paths);
        }

        private static byte[] CreateBytes(bool includeReviewedCustomSchema)
        {
            var types = new[]
            {
                GameObjectType(),
                TransformType(),
                MonoBehaviourType(includeReviewedCustomSchema),
                MonoScriptType(),
                BuiltInComponentType(),
                BuildSettingsType()
            };
            var objects = new[]
            {
                new FixtureObject(101, 0, GameObjectPayload(
                    "Fixture Root",
                    [PPtr(0, 102), PPtr(0, 103), PPtr(0, 107)],
                    7,
                    3,
                    true)),
                new FixtureObject(102, 1, TransformPayload(101, [106], 0, 0)),
                new FixtureObject(103, 2, MonoBehaviourPayload(includeReviewedCustomSchema)),
                new FixtureObject(104, 3, MonoScriptPayload()),
                new FixtureObject(105, 0, GameObjectPayload(
                    "Fixture Child",
                    [PPtr(0, 106)],
                    8,
                    4,
                    true)),
                new FixtureObject(106, 1, TransformPayload(105, [], 102, 1)),
                new FixtureObject(107, 4, new byte[4]),
                new FixtureObject(108, 5, BuildSettingsPayload())
            };

            using var dataStream = new MemoryStream();
            using (var dataWriter = new BinaryWriter(dataStream, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var item in objects)
                {
                    Align(dataWriter, 8);
                    item.ByteOffset = dataWriter.BaseStream.Position;
                    dataWriter.Write(item.Payload);
                }
            }

            using var metadataStream = new MemoryStream();
            using (var writer = new BinaryWriter(metadataStream, Encoding.UTF8, leaveOpen: true))
            {
                WriteNullTerminated(writer, UnityVersion);
                writer.Write(19u);
                writer.Write(true);
                writer.Write(types.Length);
                foreach (var type in types)
                    WriteType(writer, type);
                writer.Write(objects.Length);
                Align(writer, 4);
                foreach (var item in objects)
                {
                    Align(writer, 4);
                    writer.Write(item.PathId);
                    writer.Write(item.ByteOffset);
                    writer.Write(item.Payload.Length);
                    writer.Write(item.TypeIndex);
                }
                writer.Write(0);
                writer.Write(2);
                WriteExternal(writer, "archive:/CAB-fixture/sharedassets0.assets");
                WriteExternal(writer, "archive:/CAB-fixture/missing.assets");
                writer.Write(0);
                WriteNullTerminated(writer, "sanitized-integration-fixture");
            }

            var metadata = metadataStream.ToArray();
            var data = dataStream.ToArray();
            const int headerSize = 48;
            var dataOffset = Align(headerSize + metadata.Length, 16);
            var fileSize = dataOffset + data.Length;
            using var result = new MemoryStream(fileSize);
            using (var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true))
            {
                WriteBigEndian(writer, 0u);
                WriteBigEndian(writer, 0u);
                WriteBigEndian(writer, (uint)SerializedFileVersion);
                WriteBigEndian(writer, 0u);
                writer.Write(false);
                writer.Write(new byte[3]);
                WriteBigEndian(writer, (uint)metadata.Length);
                WriteBigEndian(writer, (long)fileSize);
                WriteBigEndian(writer, (long)dataOffset);
                writer.Write(new byte[8]);
                writer.Write(metadata);
                while (writer.BaseStream.Position < dataOffset)
                    writer.Write((byte)0);
                writer.Write(data);
            }
            return result.ToArray();
        }

        private static FixtureType GameObjectType() => new(1,
        [
            Node(0, "GameObject", "Base"),
            Node(1, "vector", "m_Component"),
            Node(2, "Array", "Array", isArray: true),
            Node(3, "int", "size"),
            Node(3, "ComponentPair", "data"),
            Node(4, "PPtr<Component>", "component"),
            Node(5, "int", "m_FileID"),
            Node(5, "SInt64", "m_PathID"),
            Node(1, "unsigned int", "m_Layer"),
            Node(1, "string", "m_Name"),
            Node(2, "Array", "Array", isArray: true),
            Node(3, "int", "size"),
            Node(3, "char", "data"),
            Node(1, "UInt16", "m_Tag"),
            Node(1, "bool", "m_IsActive", aligned: true)
        ]);

        private static FixtureType TransformType() => new(4,
        [
            Node(0, "Transform", "Base"),
            .. PPtrNodes(1, "PPtr<GameObject>", "m_GameObject"),
            Node(1, "Quaternionf", "m_LocalRotation"),
            Node(2, "float", "x"), Node(2, "float", "y"),
            Node(2, "float", "z"), Node(2, "float", "w"),
            Node(1, "Vector3f", "m_LocalPosition"),
            Node(2, "float", "x"), Node(2, "float", "y"), Node(2, "float", "z"),
            Node(1, "Vector3f", "m_LocalScale"),
            Node(2, "float", "x"), Node(2, "float", "y"), Node(2, "float", "z"),
            Node(1, "vector", "m_Children"),
            Node(2, "Array", "Array", isArray: true),
            Node(3, "int", "size"),
            Node(3, "PPtr<Transform>", "data"),
            Node(4, "int", "m_FileID"), Node(4, "SInt64", "m_PathID"),
            .. PPtrNodes(1, "PPtr<Transform>", "m_Father"),
            Node(1, "int", "m_RootOrder")
        ]);

        private static FixtureType MonoBehaviourType(bool includeReviewedCustomSchema)
        {
            List<FixtureNode> nodes =
            [
                Node(0, "MonoBehaviour", "Base"),
                .. PPtrNodes(1, "PPtr<GameObject>", "m_GameObject"),
                Node(1, "UInt8", "m_Enabled", aligned: true),
                .. PPtrNodes(1, "PPtr<MonoScript>", "m_Script"),
                Node(1, "string", "m_Name"),
                Node(2, "Array", "Array", isArray: true),
                Node(3, "int", "size"), Node(3, "char", "data")
            ];
            if (includeReviewedCustomSchema)
            {
                nodes.AddRange(PPtrNodes(1, "PPtr<GameObject>", "m_LocalTarget"));
                nodes.AddRange(PPtrNodes(1, "PPtr<GameObject>", "m_ExternalTarget"));
                nodes.AddRange(PPtrNodes(1, "PPtr<GameObject>", "m_MissingTarget"));
            }
            return new FixtureType(114, nodes.ToArray(), 0);
        }

        private static FixtureType MonoScriptType() => new(115,
        [
            Node(0, "MonoScript", "Base"),
            .. StringNodes(1, "m_Name"),
            Node(1, "int", "m_ExecutionOrder"),
            .. StringNodes(1, "m_ClassName"),
            .. StringNodes(1, "m_Namespace"),
            .. StringNodes(1, "m_AssemblyName")
        ]);

        private static FixtureType BuiltInComponentType() => new(23,
        [
            Node(0, "MeshRenderer", "Base")
        ]);

        private static FixtureType BuildSettingsType() => new(141,
        [
            Node(0, "BuildSettings", "Base"),
            Node(1, "vector", "scenes"),
            Node(2, "Array", "Array", isArray: true),
            Node(3, "int", "size"),
            Node(3, "string", "data"),
            Node(4, "Array", "Array", isArray: true),
            Node(5, "int", "size"), Node(5, "char", "data")
        ]);

        private static FixtureNode[] PPtrNodes(byte level, string type, string name) =>
        [
            Node(level, type, name),
            Node((byte)(level + 1), "int", "m_FileID"),
            Node((byte)(level + 1), "SInt64", "m_PathID")
        ];

        private static FixtureNode[] StringNodes(byte level, string name) =>
        [
            Node(level, "string", name),
            Node((byte)(level + 1), "Array", "Array", isArray: true),
            Node((byte)(level + 2), "int", "size"),
            Node((byte)(level + 2), "char", "data")
        ];

        private static byte[] GameObjectPayload(
            string name,
            IReadOnlyList<(int FileId, long PathId)> components,
            uint layer,
            ushort tag,
            bool active) => Payload(writer =>
        {
            writer.Write(components.Count);
            foreach (var component in components)
                WritePPtr(writer, component);
            writer.Write(layer);
            WriteString(writer, name);
            writer.Write(tag);
            writer.Write(active);
            Align(writer, 4);
        });

        private static byte[] TransformPayload(
            long gameObject,
            IReadOnlyList<long> children,
            long parent,
            int rootOrder) => Payload(writer =>
        {
            WritePPtr(writer, PPtr(0, gameObject));
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(1.25f); writer.Write(2.5f); writer.Write(3.75f);
            writer.Write(1f); writer.Write(1f); writer.Write(1f);
            writer.Write(children.Count);
            foreach (var child in children)
                WritePPtr(writer, PPtr(0, child));
            WritePPtr(writer, PPtr(0, parent));
            writer.Write(rootOrder);
        });

        private static byte[] MonoBehaviourPayload(bool includeReviewedCustomSchema) => Payload(writer =>
        {
            WritePPtr(writer, PPtr(0, 101));
            writer.Write((byte)1);
            Align(writer, 4);
            WritePPtr(writer, PPtr(0, 104));
            WriteString(writer, "Fixture Component");
            if (includeReviewedCustomSchema)
            {
                WritePPtr(writer, PPtr(0, 101));
                WritePPtr(writer, PPtr(1, 101));
                WritePPtr(writer, PPtr(2, 999));
            }
            else
            {
                writer.Write(Encoding.UTF8.GetBytes("opaque-unreviewed-custom-value"));
            }
        });

        private static byte[] MonoScriptPayload() => Payload(writer =>
        {
            WriteString(writer, "SceneGraphBehaviour");
            writer.Write(0);
            WriteString(writer, "SceneGraphBehaviour");
            WriteString(writer, "Fixture.Namespace");
            WriteString(writer, "Assembly-CSharp.dll");
        });

        private static byte[] BuildSettingsPayload() => Payload(writer =>
        {
            writer.Write(1);
            WriteString(writer, "Assets/Scenes/FixtureScene.unity");
        });

        private static byte[] Payload(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                write(writer);
            return stream.ToArray();
        }

        private static (int FileId, long PathId) PPtr(int fileId, long pathId) =>
            (fileId, pathId);

        private static void WritePPtr(
            BinaryWriter writer,
            (int FileId, long PathId) pointer)
        {
            writer.Write(pointer.FileId);
            writer.Write(pointer.PathId);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            Align(writer, 4);
        }

        private static FixtureNode Node(
            byte level,
            string type,
            string name,
            bool isArray = false,
            bool aligned = false) =>
            new(level, type, name, isArray, aligned);

        private static void WriteType(BinaryWriter writer, FixtureType type)
        {
            writer.Write(type.ClassId);
            writer.Write(false);
            writer.Write(type.ScriptTypeIndex);
            if (type.ClassId == 114)
                writer.Write(new byte[16]);
            writer.Write(new byte[16]);
            var stringBuffer = BuildStringBuffer(type.Nodes, out var offsets);
            writer.Write(type.Nodes.Count);
            writer.Write(stringBuffer.Length);
            for (var index = 0; index < type.Nodes.Count; index++)
            {
                var node = type.Nodes[index];
                writer.Write((ushort)1);
                writer.Write(node.Level);
                writer.Write(node.IsArray ? (byte)1 : (byte)0);
                writer.Write(offsets[node.Type]);
                writer.Write(offsets[node.Name]);
                writer.Write(-1);
                writer.Write((uint)index);
                writer.Write(node.Aligned ? 0x4000u : 0u);
                writer.Write(0ul);
            }
            writer.Write(stringBuffer);
            writer.Write(0);
        }

        private static byte[] BuildStringBuffer(
            IReadOnlyList<FixtureNode> nodes,
            out Dictionary<string, uint> offsets)
        {
            offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            using var stream = new MemoryStream();
            foreach (var value in nodes.SelectMany(node => new[] { node.Type, node.Name }))
            {
                if (offsets.ContainsKey(value))
                    continue;
                offsets.Add(value, checked((uint)stream.Position));
                stream.Write(Encoding.UTF8.GetBytes(value));
                stream.WriteByte(0);
            }
            return stream.ToArray();
        }

        private static int Align(int value, int alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private static void Align(BinaryWriter writer, int alignment)
        {
            while (writer.BaseStream.Position % alignment != 0)
                writer.Write((byte)0);
        }

        private static void WriteNullTerminated(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }

        private static void WriteExternal(BinaryWriter writer, string path)
        {
            WriteNullTerminated(writer, string.Empty);
            writer.Write(new byte[16]);
            writer.Write(0);
            WriteNullTerminated(writer, path);
        }

        private static void WriteBigEndian(BinaryWriter writer, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            writer.Write(bytes);
        }

        private static void WriteBigEndian(BinaryWriter writer, long value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            writer.Write(bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
        }

        private sealed record FixtureType(
            int ClassId,
            IReadOnlyList<FixtureNode> Nodes,
            ushort ScriptTypeIndex = ushort.MaxValue);

        private sealed record FixtureNode(
            byte Level,
            string Type,
            string Name,
            bool IsArray,
            bool Aligned);

        private sealed class FixtureObject(long pathId, int typeIndex, byte[] payload)
        {
            public long PathId { get; } = pathId;
            public int TypeIndex { get; } = typeIndex;
            public byte[] Payload { get; } = payload;
            public long ByteOffset { get; set; }
        }
    }

    private sealed class RejectingNetworkHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class RejectingProcessExtractor : IIl2CppExtractor
    {
        public int CallCount { get; private set; }

        public Task<ExtractionProcessResult> ExtractAsync(
            ExtractionProcessRequest request,
            Func<int, CancellationToken, Task> processStarted,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException(
                "Process extraction is forbidden in scene query integration tests.");
        }
    }
}
