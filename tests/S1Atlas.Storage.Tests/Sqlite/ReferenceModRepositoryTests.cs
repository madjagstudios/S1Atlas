using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class ReferenceModRepositoryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-mod-repository-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public ReferenceModRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Reference_index_round_trips_context_mods_documents_and_cross_snapshot_game_targets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var environmentSnapshotId = await SeedEnvironmentAsync(cancellationToken);

        const string buildId = "build-1";
        await SeedGameIndexAsync("snapshot-game", "index-game", environmentSnapshotId, "game-extraction", "game-symbol", cancellationToken);

        var referenceSnapshot = new CodeSnapshotRecord(
            "snapshot-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "reference-collection-1",
            "2026-08-27T00:10:00Z",
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(referenceSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-reference", referenceSnapshot.SnapshotId, IndexRunStatus.Running, referenceSnapshot.CreatedAtUtc),
            cancellationToken);

        var referenceSymbol = new IndexSymbolRecord(
            "reference-symbol",
            referenceSnapshot.SnapshotId,
            "ReferenceMod:Installed:Method:Demo.Mod::Run()",
            "Method",
            "Demo.Mod.Run",
            "System.Void Demo.Mod::Run()",
            false);
        var context = new ReferenceIndexContextRecord("index-reference", "index-game", buildId);
        var mod = new IndexReferenceModRecord(
            "mod-a",
            "Mod A",
            "1.0.0",
            "MIT",
            "C:\\Mods\\ModA",
            "content-sha-mod-a",
            ["reference-symbol"]);
        var documents = new[]
        {
            new IndexReferenceDocumentRecord("mod-a", "README.md", "Readme", "doc-readme", 24, "mod readme content"),
            new IndexReferenceDocumentRecord("mod-a", "docs/hooks.md", "Guide", "doc-guide", 42, "hook guide content")
        };
        var relationships = new[]
        {
            new IndexRelationshipRecord(
                "ref-calls-game",
                referenceSnapshot.SnapshotId,
                "reference-symbol",
                "game-symbol",
                null,
                "Calls",
                "IL:call")
        };

        await _repository.CompleteIndexRunAsync(
            "index-reference",
            new IndexWriteSet(
                [referenceSymbol],
                [],
                [],
                [],
                relationships,
                null,
                context,
                [mod],
                documents),
            "2026-08-27T00:11:00Z",
            cancellationToken);

        Assert.Equal(context, await _repository.GetReferenceIndexContextAsync("index-reference", cancellationToken));
        var storedMod = Assert.Single(await _repository.GetCompletedReferenceModsAsync("index-reference", cancellationToken));
        Assert.Equal(mod.ModId, storedMod.ModId);
        Assert.Equal(mod.DisplayName, storedMod.DisplayName);
        Assert.Equal(mod.Version, storedMod.Version);
        Assert.Equal(mod.License, storedMod.License);
        Assert.Equal(mod.RootPath, storedMod.RootPath);
        Assert.Equal(mod.ContentSha256, storedMod.ContentSha256);
        Assert.Equal(mod.SymbolIds, storedMod.SymbolIds);
        Assert.Equal(documents.OrderBy(document => document.RelativePath, StringComparer.Ordinal), await _repository.GetCompletedReferenceDocumentsAsync("index-reference", cancellationToken));
        Assert.Equal(
            ["README.md"],
            (await _repository.SearchCompletedReferenceDocumentsAsync("index-reference", "readme", 10, cancellationToken))
            .Select(document => document.RelativePath));
        Assert.Null(await _repository.GetReferenceIndexContextAsync("index-game", cancellationToken));
        Assert.Empty(await _repository.GetCompletedReferenceModsAsync("index-game", cancellationToken));
        Assert.Empty(await _repository.GetCompletedReferenceDocumentsAsync("index-game", cancellationToken));

        var storedRelationship = Assert.Single(await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(
            "index-reference",
            "reference-symbol",
            cancellationToken));
        Assert.Equal("game-symbol", storedRelationship.TargetSymbolId);
    }

    [Fact]
    public async Task Reference_documents_reject_duplicate_mod_relative_path_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var environmentSnapshotId = await SeedEnvironmentAsync(cancellationToken);
        await SeedGameIndexAsync("snapshot-game", "index-game", environmentSnapshotId, "game-extraction", "game-symbol", cancellationToken);

        var referenceSnapshot = new CodeSnapshotRecord(
            "snapshot-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "reference-collection-duplicate",
            "2026-08-27T00:20:00Z",
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(referenceSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-reference", referenceSnapshot.SnapshotId, IndexRunStatus.Running, referenceSnapshot.CreatedAtUtc),
            cancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _repository.CompleteIndexRunAsync(
                "index-reference",
                new IndexWriteSet(
                    [new IndexSymbolRecord("reference-symbol", referenceSnapshot.SnapshotId, "ReferenceMod:Installed:Type:Demo.Mod", "Type", "Demo.Mod", "Demo.Mod", false)],
                    [],
                    [],
                    [],
                    [],
                    null,
                    new ReferenceIndexContextRecord("index-reference", "index-game", "build-1"),
                    [new IndexReferenceModRecord("mod-a", "Mod A", "1.0.0", null, "C:\\Mods\\ModA", "mod-content", ["reference-symbol"])],
                    [
                        new IndexReferenceDocumentRecord("mod-a", "README.md", "Readme", "doc-1", 10, "first"),
                        new IndexReferenceDocumentRecord("mod-a", "README.md", "Readme", "doc-2", 11, "second")
                    ]),
                "2026-08-27T00:21:00Z",
                cancellationToken));
    }

    [Fact]
    public async Task Reference_index_rejects_relationship_target_outside_reference_or_recorded_game_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var environmentSnapshotId = await SeedEnvironmentAsync(cancellationToken);

        await SeedGameIndexAsync("snapshot-game-a", "index-game-a", environmentSnapshotId, "game-extraction-a", "game-symbol-a", cancellationToken);
        await SeedGameIndexAsync("snapshot-game-b", "index-game-b", environmentSnapshotId, "game-extraction-b", "game-symbol-b", cancellationToken);

        var referenceSnapshot = new CodeSnapshotRecord(
            "snapshot-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "reference-collection-invalid-target",
            "2026-08-27T00:30:00Z",
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(referenceSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-reference", referenceSnapshot.SnapshotId, IndexRunStatus.Running, referenceSnapshot.CreatedAtUtc),
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CompleteIndexRunAsync(
                "index-reference",
                new IndexWriteSet(
                    [new IndexSymbolRecord("reference-symbol", referenceSnapshot.SnapshotId, "ReferenceMod:Installed:Method:Demo.Mod::Run()", "Method", "Demo.Mod.Run", "System.Void Demo.Mod::Run()", false)],
                    [],
                    [],
                    [],
                    [
                        new IndexRelationshipRecord(
                            "invalid-target",
                            referenceSnapshot.SnapshotId,
                            "reference-symbol",
                            "game-symbol-b",
                            null,
                            "Calls",
                            "IL:call")
                    ],
                    null,
                    new ReferenceIndexContextRecord("index-reference", "index-game-a", "build-1"),
                    [new IndexReferenceModRecord("mod-a", "Mod A", "1.0.0", null, "C:\\Mods\\ModA", "mod-content", ["reference-symbol"])],
                    [new IndexReferenceDocumentRecord("mod-a", "README.md", "Readme", "doc-1", 10, "first")]),
                "2026-08-27T00:31:00Z",
                cancellationToken));

        await _repository.FailIndexRunAsync("index-reference", "cleanup", "2026-08-27T00:32:00Z", cancellationToken);
        Assert.Empty(await _repository.GetCompletedSymbolsAsync("index-reference", cancellationToken));
        Assert.Empty(await _repository.GetCompletedReferenceDocumentsAsync("index-reference", cancellationToken));
    }

    [Fact]
    public async Task Reference_mod_ownership_failure_rolls_back_transaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var environmentSnapshotId = await SeedEnvironmentAsync(cancellationToken);
        await SeedGameIndexAsync("snapshot-game", "index-game", environmentSnapshotId, "game-extraction", "game-symbol", cancellationToken);

        var referenceSnapshot = new CodeSnapshotRecord(
            "snapshot-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "reference-collection-owner",
            "2026-08-27T00:40:00Z",
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(referenceSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-reference", referenceSnapshot.SnapshotId, IndexRunStatus.Running, referenceSnapshot.CreatedAtUtc),
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CompleteIndexRunAsync(
                "index-reference",
                new IndexWriteSet(
                    [new IndexSymbolRecord("reference-symbol", referenceSnapshot.SnapshotId, "ReferenceMod:Installed:Type:Demo.Mod", "Type", "Demo.Mod", "Demo.Mod", false)],
                    [],
                    [],
                    [],
                    [],
                    null,
                    new ReferenceIndexContextRecord("index-reference", "index-game", "build-1"),
                    [new IndexReferenceModRecord("mod-a", "Mod A", "1.0.0", null, "C:\\Mods\\ModA", "mod-content", ["game-symbol"])],
                    [new IndexReferenceDocumentRecord("mod-a", "README.md", "Readme", "doc-1", 10, "first")]),
                "2026-08-27T00:41:00Z",
                cancellationToken));

        await _repository.FailIndexRunAsync("index-reference", "cleanup", "2026-08-27T00:42:00Z", cancellationToken);
        Assert.Empty(await _repository.GetCompletedSymbolsAsync("index-reference", cancellationToken));
        Assert.Empty(await _repository.GetCompletedReferenceModsAsync("index-reference", cancellationToken));
        Assert.Empty(await _repository.GetCompletedReferenceDocumentsAsync("index-reference", cancellationToken));
    }

    private async Task<string> SeedEnvironmentAsync(CancellationToken cancellationToken)
    {
        var snapshot = new EnvironmentSnapshot(
            2,
            new GameBuild("build-1", "assembly-1", "metadata-1", DateTimeOffset.Parse("2026-08-27T00:00:00Z"), true),
            new InstallationObservation("1.0.0", "3164500", "9001", "C:\\Game", "C:\\Game\\GameAssembly.dll", "C:\\Game\\global-metadata.dat"),
            [],
            "0.3.0-test",
            DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        await _repository.SaveSnapshotAsync(snapshot, cancellationToken);
        return EnvironmentSnapshotId.Create(snapshot);
    }

    private async Task SeedGameIndexAsync(
        string snapshotId,
        string indexId,
        string environmentSnapshotId,
        string sourceIdentity,
        string symbolId,
        CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            sourceIdentity,
            "2026-08-27T00:01:00Z",
            environmentSnapshotId);
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [new IndexSymbolRecord(symbolId, snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Game::Run()", "Method", "Demo.Game.Run", "System.Void Demo.Game::Run()", false, null, true)],
                [],
                [],
                [],
                []),
            "2026-08-27T00:02:00Z",
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
