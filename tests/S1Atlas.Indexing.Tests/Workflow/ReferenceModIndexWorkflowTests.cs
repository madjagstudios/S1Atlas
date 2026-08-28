using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class ReferenceModIndexWorkflowTests
{
    [Fact]
    public async Task Indexes_only_selected_local_mods_and_reuses_persisted_game_symbols()
    {
        await using var fixture = await ReferenceWorkflowFixture.CreateAsync(includeEnvironmentLink: false);
        var selectedAssembly = fixture.CreateInput("qol", "plugins/QolMod.dll", "selected");
        fixture.CreateInput("qol", "README.md", "local readme");
        fixture.CreateInput("qol", "notes/DEVLOG.md", "local development log");
        fixture.CreateInput("omitted", "plugins/OmittedMod.dll", "omitted");
        var decompiler = new RecordingDecompiler(fixture.CreateModDecompilation("Qol.Mod"));
        var workflow = fixture.CreateWorkflow(decompiler);
        var collection = fixture.Collection(
            new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", "MIT", fixture.ModRoot("qol"), "qol-content", ["plugins/**", "**/*.md"]),
            new ReferenceModDefinition("omitted", "Omitted", "1.0.0", null, fixture.ModRoot("omitted"), "omitted-content", ["nothing/**"]));

        var result = await workflow.RunAsync(fixture.BuildId, collection, false, TestContext.Current.CancellationToken);

        Assert.False(result.Reused);
        Assert.Equal(1, result.ReferenceModCount);
        Assert.Equal(2, result.ReferenceDocumentCount);
        Assert.True(result.ReferenceSymbolCount > 0);
        Assert.Equal([Path.GetFullPath(selectedAssembly)], decompiler.Paths);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "reference", result.IndexId, "qol", "plugins", "QolMod.cs")));
        Assert.DoesNotContain(decompiler.Paths, path => path.Contains("game", StringComparison.OrdinalIgnoreCase));

        var mods = await fixture.Repository.GetCompletedReferenceModsAsync(result.IndexId, TestContext.Current.CancellationToken);
        var mod = Assert.Single(mods);
        Assert.Equal("qol", mod.ModId);
        Assert.Equal(collection.GameIndexId, (await fixture.Repository.GetReferenceIndexContextAsync(result.IndexId, TestContext.Current.CancellationToken))!.GameIndexId);
        Assert.Equal(
            ["Devlog", "Readme"],
            (await fixture.Repository.GetCompletedReferenceDocumentsAsync(result.IndexId, TestContext.Current.CancellationToken))
            .Select(document => document.Kind)
            .Order(StringComparer.Ordinal));
        var relationship = Assert.Single(
            await fixture.Repository.GetCompletedRelationshipsAsync(result.IndexId, TestContext.Current.CancellationToken),
            edge => edge.Kind == "Calls");
        Assert.Equal(fixture.GameSymbolId, relationship.TargetSymbolId);
    }

    [Fact]
    public async Task Reuses_only_matching_completed_reference_indexes_and_force_creates_a_candidate()
    {
        await using var fixture = await ReferenceWorkflowFixture.CreateAsync();
        fixture.CreateInput("qol", "plugins/QolMod.dll", "selected");
        var workflow = fixture.CreateWorkflow(new RecordingDecompiler(fixture.CreateModDecompilation("Qol.Mod")));
        var collection = fixture.Collection(new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, fixture.ModRoot("qol"), "qol-content", ["plugins/**"]));

        var first = await workflow.RunAsync(fixture.BuildId, collection, false, TestContext.Current.CancellationToken);
        var reused = await workflow.RunAsync(fixture.BuildId, collection, false, TestContext.Current.CancellationToken);
        var changedManifest = collection with
        {
            Mods = [collection.Mods[0] with { ContentSha256 = "changed-declared-content-sha" }]
        };
        var changed = await workflow.RunAsync(fixture.BuildId, changedManifest, false, TestContext.Current.CancellationToken);
        var forced = await workflow.RunAsync(fixture.BuildId, collection, true, TestContext.Current.CancellationToken);

        Assert.False(first.Reused);
        Assert.True(reused.Reused);
        Assert.Equal(first.IndexId, reused.IndexId);
        Assert.False(changed.Reused);
        Assert.NotEqual(first.IndexId, changed.IndexId);
        Assert.False(forced.Reused);
        Assert.NotEqual(first.IndexId, forced.IndexId);
    }

    [Fact]
    public async Task Reference_identity_uses_hashes_and_not_absolute_mod_paths()
    {
        await using var first = await ReferenceWorkflowFixture.CreateAsync();
        await using var second = await ReferenceWorkflowFixture.CreateAsync();
        first.CreateInput("qol", "plugins/QolMod.dll", "same bytes");
        second.CreateInput("qol", "plugins/QolMod.dll", "same bytes");
        var hasher = new ReferenceModInputHasher();

        var firstHash = await hasher.HashAsync(new ReferenceModFileSelector().Select([new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, first.ModRoot("qol"), "qol-content", ["plugins/**"])]), TestContext.Current.CancellationToken);
        var secondHash = await hasher.HashAsync(new ReferenceModFileSelector().Select([new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, second.ModRoot("qol"), "qol-content", ["plugins/**"])]), TestContext.Current.CancellationToken);

        var firstCollection = first.Collection(new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, first.ModRoot("qol"), "declared-content-sha", ["plugins/**"]));
        var secondCollection = second.Collection(new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, second.ModRoot("qol"), "declared-content-sha", ["plugins/**"]));
        var firstId = ReferenceModIndexWorkflow.CreateIndexId(first.GameIndexId, first.ExtractionIdentity, ReferenceModIndexWorkflow.CreateCollectionHash(firstCollection, firstHash.CollectionContentSha256), "reference", IndexingWorkflow.IndexSchemaVersion);
        var secondId = ReferenceModIndexWorkflow.CreateIndexId(second.GameIndexId, second.ExtractionIdentity, ReferenceModIndexWorkflow.CreateCollectionHash(secondCollection, secondHash.CollectionContentSha256), "reference", IndexingWorkflow.IndexSchemaVersion);

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task Deletes_staging_and_does_not_complete_when_local_input_drifts_after_read()
    {
        await using var fixture = await ReferenceWorkflowFixture.CreateAsync();
        var assembly = fixture.CreateInput("qol", "plugins/QolMod.dll", "before");
        var decompiler = new RecordingDecompiler(fixture.CreateModDecompilation("Qol.Mod"), () => File.WriteAllText(assembly, "after"));
        var workflow = fixture.CreateWorkflow(decompiler);
        var collection = fixture.Collection(new ReferenceModDefinition("qol", "Quality of Life", "1.0.0", null, fixture.ModRoot("qol"), "qol-content", ["plugins/**"]));
        var inputHash = await new ReferenceModInputHasher().HashAsync(new ReferenceModFileSelector().Select(collection.Mods), TestContext.Current.CancellationToken);
        var indexId = ReferenceModIndexWorkflow.CreateIndexId(fixture.GameIndexId, fixture.ExtractionIdentity, ReferenceModIndexWorkflow.CreateCollectionHash(collection, inputHash.CollectionContentSha256), "reference", IndexingWorkflow.IndexSchemaVersion);

        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.RunAsync(fixture.BuildId, collection, false, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "reference", indexId + ".staging")));
        Assert.Null(await fixture.Repository.GetCompletedIndexAsync(indexId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Loads_normal_completed_schedule_one_symbols_without_an_environment_link()
    {
        await using var fixture = await ReferenceWorkflowFixture.CreateAsync(includeEnvironmentLink: false);

        var loaded = await new ReferenceGameSymbolLoader(fixture.Repository)
            .LoadAsync(fixture.GameIndexId, TestContext.Current.CancellationToken);

        Assert.Equal(fixture.GameIndexId, loaded.IndexId);
        Assert.Equal([fixture.GameSymbolId], loaded.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal(fixture.ExtractionIdentity, loaded.VerifiedExtractionIdentity);
    }

    private sealed class RecordingDecompiler : IManagedDecompiler
    {
        private readonly ManagedDecompilation _decompilation;
        private readonly Action? _afterRead;

        public RecordingDecompiler(ManagedDecompilation decompilation, Action? afterRead = null)
        {
            _decompilation = decompilation;
            _afterRead = afterRead;
        }

        public List<string> Paths { get; } = [];

        public Task<ManagedDecompilation> DecompileAsync(string assemblyPath, CancellationToken cancellationToken)
        {
            Paths.Add(Path.GetFullPath(assemblyPath));
            _afterRead?.Invoke();
            return Task.FromResult(_decompilation with { AssemblyPath = Path.GetFullPath(assemblyPath) });
        }
    }

    private sealed class ReferenceWorkflowFixture : IAsyncDisposable
    {
        private ReferenceWorkflowFixture(string root, SqliteAtlasRepository repository)
        {
            Root = root;
            Repository = repository;
        }

        public string BuildId => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public string GameIndexId => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public string GameSymbolId => "game-symbol-id";
        public string ExtractionIdentity => "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        public string Root { get; }
        public SqliteAtlasRepository Repository { get; }

        public static async Task<ReferenceWorkflowFixture> CreateAsync(bool includeEnvironmentLink = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new ReferenceWorkflowFixture(root, new SqliteAtlasRepository(Path.Combine(root, "atlas.db")));
            await fixture.Repository.InitializeAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var environment = new EnvironmentSnapshot(2, new GameBuild(fixture.BuildId, "assembly", "metadata", now, true), new InstallationObservation("1", "2", "fixture", root, null, null), [], "test", now);
            await fixture.Repository.SaveSnapshotAsync(environment, TestContext.Current.CancellationToken);
            string? environmentId = includeEnvironmentLink ? EnvironmentSnapshotId.Create(environment) : null;
            var snapshot = new CodeSnapshotRecord("game-snapshot", CodebaseKind.ScheduleI, CodeChannel.Installed, fixture.ExtractionIdentity, now.ToString("O"), environmentId);
            await fixture.Repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
            await fixture.Repository.StartIndexRunAsync(new IndexRunRecord(fixture.GameIndexId, snapshot.SnapshotId, IndexRunStatus.Running, now.ToString("O")), TestContext.Current.CancellationToken);
            await fixture.Repository.CompleteIndexRunAsync(fixture.GameIndexId, new IndexWriteSet([
                new IndexSymbolRecord(fixture.GameSymbolId, snapshot.SnapshotId, "ScheduleI:Installed:Method:Game.Target::Run():System.Void", "Method", "Game.Target::Run():System.Void", "Game.Target::Run():System.Void", false)
            ], [], [], [], []), now.ToString("O"), TestContext.Current.CancellationToken);
            return fixture;
        }

        public string ModRoot(string modId) => Path.Combine(Root, "mods", modId);

        public string CreateInput(string modId, string relativePath, string content)
        {
            var path = Path.Combine(ModRoot(modId), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public ReferenceCollectionDefinition Collection(params ReferenceModDefinition[] mods) => new(BuildId, GameIndexId, mods, "fixture-collection", "Fixture Collection");

        public ReferenceModIndexWorkflow CreateWorkflow(IManagedDecompiler decompiler) => new(Root, Repository, new ReferenceModFileSelector(), new ReferenceModInputHasher(), new ReferenceModIndexSource(decompiler), new ReferenceGameSymbolLoader(Repository));

        public ManagedDecompilation CreateModDecompilation(string typeName) => new(
            "fixture.dll",
            "namespace Qol; public class Mod { public void Run() {} }",
            [new ManagedTypeFacts(typeName, "Qol", "Mod", null, [], [new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Run", true, [new ManagedReferenceFact(ManagedReferenceKind.Calls, "Game.Target::Run():System.Void")], [], "System.Void")])]);

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
