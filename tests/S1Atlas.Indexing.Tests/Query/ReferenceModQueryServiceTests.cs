using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class ReferenceModQueryServiceTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-query-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly SqliteAtlasRepository _repository;

    public ReferenceModQueryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Reference_search_requires_collection_and_preserves_mod_provenance()
    {
        var fixture = await SeedAsync("qol", "Quality of Life");
        var service = new ReferenceModQueryService(_repository, _dataRoot);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            "mod",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference),
            TestContext.Current.CancellationToken));

        var result = await service.SearchAsync(
            "Qol.Mod::Run",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId),
            TestContext.Current.CancellationToken);

        var symbol = Assert.Single(result.Results);
        Assert.Equal(SymbolResolutionStatus.Resolved, result.ResolutionStatus);
        Assert.Equal("reference", symbol.Origin);
        Assert.Equal(fixture.IndexId, symbol.Collection);
        Assert.Equal("qol", symbol.ReferenceModId);
        Assert.Equal("qol/plugins/QolMod.cs", symbol.RelativePath);
        Assert.False(string.IsNullOrWhiteSpace(symbol.Sha256));
        Assert.Equal("Quality of Life", symbol.DisplayName);
        Assert.Equal("1.0.0", symbol.Version);
        Assert.Equal("MIT", symbol.License);
    }

    [Fact]
    public async Task Missing_or_incomplete_reference_collection_returns_no_completed_index()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var result = await service.SearchAsync(
            "anything",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: "missing-collection"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, result.ResolutionStatus);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task Reference_source_and_documents_are_bounded_and_hash_verified()
    {
        var fixture = await SeedAsync("docs", "Docs");
        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var options = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId);

        var source = await service.SourceAsync("qol/Qol.Mod::Run():System.Void", options, 0, TestContext.Current.CancellationToken);
        Assert.Equal(SymbolResolutionStatus.Resolved, source.Resolution.Status);
        Assert.Null(source.Snippet);

        var documents = await service.GetDocumentsAsync(options, TestContext.Current.CancellationToken);
        var document = Assert.Single(documents);
        Assert.True(document.Content.Length <= ReferenceModQueryService.MaxDocumentExcerptCharacters);
        Assert.Equal("qol", document.ReferenceModId);
        Assert.Equal("docs/README.md", document.RelativePath);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "atlas.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE reference_documents SET content = 'tampered' WHERE index_id = $id;";
            command.Parameters.AddWithValue("$id", fixture.IndexId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDocumentsAsync(
            options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reference_source_does_not_guess_a_file_for_a_multi_assembly_mod()
    {
        var fixture = await SeedAsync("multi-assembly", "Multi assembly", multipleAssemblies: true);
        var service = new ReferenceModQueryService(_repository, _dataRoot);

        var result = await service.SourceAsync(
            "qol/Qol.Mod::Run():System.Void",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId),
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Null(result.Snippet);
        Assert.Equal(2, (await _repository.GetCompletedSourceFilesAsync(fixture.IndexId, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Reference_source_uses_the_persisted_location_for_the_matching_assembly()
    {
        var fixture = await SeedAsync("located-assembly", "Located assembly", multipleAssemblies: true);
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(fixture.IndexId, TestContext.Current.CancellationToken);
        var second = Assert.Single(sourceFiles, file => file.RelativePath.EndsWith("QolMod.Second.cs", StringComparison.Ordinal));
        var symbol = Assert.Single(await _repository.GetCompletedSymbolsAsync(fixture.IndexId, TestContext.Current.CancellationToken), item => item.QualifiedName == "qol/Qol.Mod");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "atlas.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO source_locations(symbol_id, source_file_id, start_line, start_column, end_line, end_column) VALUES ($symbol,$file,1,1,2,57);";
            command.Parameters.AddWithValue("$symbol", symbol.SymbolId);
            command.Parameters.AddWithValue("$file", second.SourceFileId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var result = await service.SourceAsync(
            "qol/Qol.Mod",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId),
            0,
            TestContext.Current.CancellationToken);

        var snippet = Assert.IsType<SourceSnippetQueryResult>(result.Snippet);
        Assert.Equal(second.RelativePath, snippet.RelativePath);
        Assert.Equal(second.Sha256, snippet.Sha256);
        Assert.Contains("SECOND ASSEMBLY", snippet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_source_rejects_a_reparse_point_ancestor()
    {
        var fixture = await SeedAsync("reparse-ancestor", "Reparse ancestor", multipleAssemblies: true);
        var sourceFiles = await _repository.GetCompletedSourceFilesAsync(fixture.IndexId, TestContext.Current.CancellationToken);
        var second = Assert.Single(sourceFiles, file => file.RelativePath.EndsWith("QolMod.Second.cs", StringComparison.Ordinal));
        var symbol = Assert.Single(await _repository.GetCompletedSymbolsAsync(fixture.IndexId, TestContext.Current.CancellationToken), item => item.QualifiedName == "qol/Qol.Mod");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "atlas.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO source_locations(symbol_id, source_file_id, start_line, start_column, end_line, end_column) VALUES ($symbol,$file,1,1,2,57);";
            command.Parameters.AddWithValue("$symbol", symbol.SymbolId);
            command.Parameters.AddWithValue("$file", second.SourceFileId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var referenceRoot = Path.Combine(_dataRoot, "reference");
        var outsideRoot = Path.Combine(_root, "reference-outside");
        Directory.Move(referenceRoot, outsideRoot);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(referenceRoot, outsideRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("The test environment does not permit directory reparse-point creation.");
            }

            var service = new ReferenceModQueryService(_repository, _dataRoot);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SourceAsync(
                "qol/Qol.Mod",
                new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId),
                0,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(referenceRoot) || File.Exists(referenceRoot))
                Directory.Delete(referenceRoot);
            Directory.Move(outsideRoot, referenceRoot);
        }
    }

    [Fact]
    public async Task Reference_relationships_cross_to_recorded_game_index_and_keep_unresolved_text()
    {
        var fixture = await SeedAsync("relationships", "Relationships");
        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var options = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId);

        var callees = await service.CalleesAsync("qol/Qol.Mod::Run():System.Void", options, TestContext.Current.CancellationToken);
        var edge = Assert.Single(callees.Relationships);
        Assert.Equal("game", edge.Target.Origin);
        Assert.Equal(fixture.GameSymbolId, edge.Target.SymbolId);
        Assert.True(edge.Target.Resolved);

        var callers = await service.CallersAsync(
            fixture.GameSymbolId,
            options,
            TestContext.Current.CancellationToken);
        var caller = Assert.Single(callers.Relationships);
        Assert.Equal("reference", caller.Source.Origin);
        Assert.Equal("qol", caller.Source.ReferenceModId);

        var unresolved = await service.CalleesAsync("qol/Qol.Mod::Unresolved():System.Void", options, TestContext.Current.CancellationToken);
        var unresolvedEdge = Assert.Single(unresolved.Relationships);
        Assert.False(unresolvedEdge.Target.Resolved);
        Assert.Equal("Missing.Target::Run():System.Void", unresolvedEdge.Target.RawText);
    }

    [Fact]
    public async Task Federation_keeps_game_and_reference_origins_and_preserves_cross_origin_ambiguity()
    {
        var fixture = await SeedAsync("federated", "Federated");
        var service = new FederatedIndexQueryService(_repository, _dataRoot);
        var referenceOptions = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId);

        var game = await service.SearchAsync(
            "Game.Target",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.Game),
            TestContext.Current.CancellationToken);
        Assert.Equal("game", Assert.Single(game.Results).Origin);

        var all = await service.SearchAsync(
            "Run",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.All, ReferenceCollection: fixture.IndexId),
            TestContext.Current.CancellationToken);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(["game", "game", "reference"], all.Results.Select(result => result.Origin!).ToArray());

        var ambiguous = await service.ResolveAsync("Run", referenceOptions with { Scope = IndexQueryScope.All }, TestContext.Current.CancellationToken);
        Assert.Equal(SymbolResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(3, ambiguous.Candidates.Count);
        Assert.Contains(ambiguous.Candidates, candidate => candidate.Origin == "game");
        Assert.Contains(ambiguous.Candidates, candidate => candidate.Origin == "reference");

        var sameGameIdentity = await service.ResolveAsync(
            "Game.Target::Run():System.Void",
            referenceOptions with { Scope = IndexQueryScope.All },
            TestContext.Current.CancellationToken);
        Assert.Equal(SymbolResolutionStatus.Resolved, sameGameIdentity.Status);
        Assert.Equal("game", sameGameIdentity.Symbol!.Origin);
        Assert.Empty(sameGameIdentity.Candidates);
    }

    [Fact]
    public async Task Federation_game_scope_rejects_reference_collection_and_all_scope_federates_relationship_origins()
    {
        var fixture = await SeedAsync("relationship-federation", "Relationship federation");
        var service = new FederatedIndexQueryService(_repository, _dataRoot);
        var all = new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.All, ReferenceCollection: fixture.IndexId);

        var callers = await service.CallersAsync(fixture.GameSymbolId, all, TestContext.Current.CancellationToken);
        Assert.Equal(["game", "reference"], callers.Relationships.Select(edge => edge.Source.Origin!).OrderBy(origin => origin, StringComparer.Ordinal).ToArray());

        var references = await service.RefsAsync(fixture.GameSymbolId, all, TestContext.Current.CancellationToken);
        Assert.Equal(["game", "reference"], references.Relationships.Select(edge => edge.Source.Origin!).OrderBy(origin => origin, StringComparer.Ordinal).ToArray());

        var gameOnly = all with { Scope = IndexQueryScope.Game };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CallersAsync(fixture.GameSymbolId, gameOnly, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RefsAsync(fixture.GameSymbolId, gameOnly, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reference_collection_lookup_accepts_the_snapshot_source_identity()
    {
        var fixture = await SeedAsync("source-identity", "Source identity");
        var snapshot = await _repository.GetCodeSnapshotAsync("reference:" + fixture.GameIndexId + ":" + fixture.IndexId, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var service = new ReferenceModQueryService(_repository, _dataRoot);

        var result = await service.SearchAsync(
            "Qol.Mod::Run",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: snapshot!.SourceIdentity),
            TestContext.Current.CancellationToken);

        Assert.Equal(snapshot.SourceIdentity, Assert.Single(result.Results).Collection);
    }

    [Fact]
    public async Task Running_reference_collection_is_not_queryable()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        var snapshot = new CodeSnapshotRecord("running-reference-snapshot", CodebaseKind.ReferenceMod, CodeChannel.Installed, "running-collection", "2026-08-28T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("running-reference-index", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), TestContext.Current.CancellationToken);
        var service = new ReferenceModQueryService(_repository, _dataRoot);

        var result = await service.SearchAsync(
            "anything",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: snapshot.SourceIdentity),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, result.ResolutionStatus);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task Reference_document_search_escapes_wildcards_and_get_documents_honors_limit()
    {
        var fixture = await SeedAsync("document-limits", "Document limits", multipleDocuments: true);
        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var options = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId, Limit: 1);

        var documents = await service.GetDocumentsAsync(options, TestContext.Current.CancellationToken);
        Assert.Single(documents);
        Assert.Equal("docs/CHANGELOG.md", documents[0].RelativePath);

        var wildcard = await service.SearchDocumentsAsync("README%", options, TestContext.Current.CancellationToken);
        Assert.Empty(wildcard);
    }

    [Fact]
    public async Task Completed_empty_collection_is_a_no_result_and_collection_selection_is_isolated()
    {
        var first = await SeedAsync("first", "First");
        var second = await SeedAsync("second", "Second");
        var empty = await SeedEmptyAsync("empty", "Empty");
        var service = new ReferenceModQueryService(_repository, _dataRoot);

        var firstOptions = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: first.IndexId);
        var secondOptions = firstOptions with { ReferenceCollection = second.IndexId };
        var emptyOptions = firstOptions with { ReferenceCollection = empty.IndexId };

        var firstResult = await service.SearchAsync("Qol.Mod::Run", firstOptions, TestContext.Current.CancellationToken);
        var secondResult = await service.SearchAsync("Qol.Mod::Run", secondOptions, TestContext.Current.CancellationToken);
        var emptyResult = await service.SearchAsync("Qol.Mod::Run", emptyOptions, TestContext.Current.CancellationToken);

        Assert.Equal(first.IndexId, Assert.Single(firstResult.Results).Collection);
        Assert.Equal(second.IndexId, Assert.Single(secondResult.Results).Collection);
        Assert.Equal(SymbolResolutionStatus.NotFound, emptyResult.ResolutionStatus);
        Assert.Empty(emptyResult.Results);
    }

    [Fact]
    public async Task Same_name_reference_symbols_remain_ambiguous_in_deterministic_order()
    {
        var fixture = await SeedAmbiguousAsync();
        var service = new ReferenceModQueryService(_repository, _dataRoot);
        var options = new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: fixture.IndexId);

        var result = await service.ResolveAsync("Qol.Mod::Run", options, TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(["alt", "qol"], result.Candidates.Select(candidate => candidate.ReferenceModId!).ToArray());
    }

    private async Task<QueryFixture> SeedAsync(string collectionName, string displayName, bool multipleAssemblies = false, bool multipleDocuments = false)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        var fixture = await QueryFixture.CreateAsync(_root, _dataRoot, _repository, collectionName, displayName, multipleAssemblies: multipleAssemblies, multipleDocuments: multipleDocuments);
        return fixture;
    }

    private async Task<QueryFixture> SeedEmptyAsync(string collectionName, string displayName)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        return await QueryFixture.CreateAsync(_root, _dataRoot, _repository, collectionName, displayName, empty: true);
    }

    private async Task<QueryFixture> SeedAmbiguousAsync()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        return await QueryFixture.CreateAsync(_root, _dataRoot, _repository, "ambiguous", "Ambiguous", secondMod: true);
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        await Task.CompletedTask;
    }

    private sealed record QueryFixture(string IndexId, string GameIndexId, string GameSymbolId)
    {
        public static async Task<QueryFixture> CreateAsync(
            string root,
            string dataRoot,
            SqliteAtlasRepository repository,
            string collectionName,
            string displayName,
            bool empty = false,
            bool secondMod = false,
            bool multipleAssemblies = false,
            bool multipleDocuments = false)
        {
            var buildId = new string('a', 64);
            var gameIndexId = new string('b', 64);
            var gameSymbolId = "game-target";
            var now = DateTimeOffset.UtcNow;
            var environment = new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly", "metadata", now, true),
                new InstallationObservation("1", "2", "fixture", root, null, null),
                [],
                "test",
                now);
            await repository.SaveSnapshotAsync(environment, TestContext.Current.CancellationToken);
            if (await repository.GetCompletedIndexAsync(gameIndexId, TestContext.Current.CancellationToken) is null)
            {
                var environmentId = EnvironmentSnapshotId.Create(environment);
                var gameSnapshot = new CodeSnapshotRecord("game-snapshot", CodebaseKind.ScheduleI, CodeChannel.Installed, "game-extraction", now.ToString("O"), environmentId);
                await repository.CreateCodeSnapshotAsync(gameSnapshot, TestContext.Current.CancellationToken);
                await repository.StartIndexRunAsync(new IndexRunRecord(gameIndexId, gameSnapshot.SnapshotId, IndexRunStatus.Running, now.ToString("O")), TestContext.Current.CancellationToken);
                var gameSymbol = new IndexSymbolRecord(
                    gameSymbolId,
                    gameSnapshot.SnapshotId,
                    "ScheduleI:Installed:Method:Game.Target::Run():System.Void",
                    "Method",
                    "Game.Target::Run():System.Void",
                    "Game.Target::Run():System.Void",
                    false);
                var gameCaller = new IndexSymbolRecord(
                    "game-caller",
                    gameSnapshot.SnapshotId,
                    "ScheduleI:Installed:Method:Game.Caller::Run():System.Void",
                    "Method",
                    "Game.Caller::Run():System.Void",
                    "Game.Caller::Run():System.Void",
                    false);
                await repository.CompleteIndexRunAsync(
                    gameIndexId,
                    new IndexWriteSet(
                        [gameSymbol, gameCaller],
                        [],
                        [],
                        [],
                        [new IndexRelationshipRecord("game-caller", gameSnapshot.SnapshotId, gameCaller.SymbolId, gameSymbol.SymbolId, null, "Calls", "fixture:game")]),
                    now.ToString("O"),
                    TestContext.Current.CancellationToken);
            }

            var modRoot = Path.Combine(root, "mods", "qol");
            Directory.CreateDirectory(Path.Combine(modRoot, "plugins"));
            Directory.CreateDirectory(Path.Combine(modRoot, "docs"));
            var assemblyPath = Path.Combine(modRoot, "plugins", "QolMod.dll");
            await File.WriteAllTextAsync(assemblyPath, "fixture assembly", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(modRoot, "docs", "README.md"), new string('x', 20000), TestContext.Current.CancellationToken);
            if (multipleDocuments)
                await File.WriteAllTextAsync(Path.Combine(modRoot, "docs", "CHANGELOG.md"), "change log", TestContext.Current.CancellationToken);
            if (multipleAssemblies)
                await File.WriteAllTextAsync(Path.Combine(modRoot, "plugins", "QolMod.Second.dll"), "second fixture assembly", TestContext.Current.CancellationToken);
            var secondRoot = Path.Combine(root, "mods", "alt");
            if (secondMod)
            {
                Directory.CreateDirectory(Path.Combine(secondRoot, "plugins"));
                await File.WriteAllTextAsync(Path.Combine(secondRoot, "plugins", "AltMod.dll"), "alternate fixture assembly", TestContext.Current.CancellationToken);
            }
            var definitions = new List<ReferenceModDefinition>();
            if (!empty)
            {
                definitions.Add(new ReferenceModDefinition("qol", displayName, "1.0.0", "MIT", modRoot, "declared", ["plugins/**", "docs/**"]));
                if (secondMod)
                    definitions.Add(new ReferenceModDefinition("alt", "Alternate", "2.0.0", "Apache-2.0", secondRoot, "declared-alt", ["plugins/**"]));
            }
            var collection = new ReferenceCollectionDefinition(
                buildId,
                gameIndexId,
                definitions,
                collectionName,
                displayName);
            var decompilation = new ManagedDecompilation(
                assemblyPath,
                "namespace Qol; public class Mod { public void Run() {} public void Unresolved() {} }",
                [new ManagedTypeFacts(
                    "Qol.Mod",
                    "Qol",
                    "Mod",
                    null,
                    [],
                    [
                        new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Run", true, [new ManagedReferenceFact(ManagedReferenceKind.Calls, "Game.Target::Run():System.Void")], [], "System.Void"),
                        new ManagedMemberFacts("Unresolved", ManagedMemberKind.Method, "Unresolved", true, [new ManagedReferenceFact(ManagedReferenceKind.Calls, "Missing.Target::Run():System.Void")], [], "System.Void")
                    ])]);
            var workflow = new ReferenceModIndexWorkflow(
                dataRoot,
                repository,
                new ReferenceModFileSelector(),
                new ReferenceModInputHasher(),
                new ReferenceModIndexSource(new RecordingDecompiler(decompilation)),
                new ReferenceGameSymbolLoader(repository));
            var result = await workflow.RunAsync(buildId, collection, false, TestContext.Current.CancellationToken);
            return new QueryFixture(result.IndexId, gameIndexId, gameSymbolId);
        }
    }

    private sealed class RecordingDecompiler(ManagedDecompilation decompilation) : IManagedDecompiler
    {
        public Task<ManagedDecompilation> DecompileAsync(string assemblyPath, CancellationToken cancellationToken) =>
            Task.FromResult(decompilation with
            {
                AssemblyPath = assemblyPath,
                SourceText = assemblyPath.EndsWith("QolMod.Second.dll", StringComparison.Ordinal)
                    ? "// SECOND ASSEMBLY\nnamespace Qol; public class Mod { public void Run() {} }"
                    : decompilation.SourceText
            });
    }
}
