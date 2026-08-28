using S1Atlas.Core.Indexing;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class FederatedTargetRelationshipQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-federated-target-query-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public FederatedTargetRelationshipQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task CallSitesAsync_uses_scope_specific_completed_indexes_and_preserves_reference_provenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new FederatedIndexQueryService(_repository);

        var game = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Game),
            cancellationToken);
        var reference = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Reference, fixture.Collection),
            cancellationToken);
        var all = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.All, fixture.Collection),
            cancellationToken);

        Assert.Equal(["game-current-callsite"], game.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(game.Relationships, edge => Assert.Equal("game", edge.Source.Origin));

        Assert.Equal(["reference-latest-callsite"], reference.Relationships.Select(edge => edge.RelationshipId));
        var referenceEdge = Assert.Single(reference.Relationships);
        Assert.Equal("reference", referenceEdge.Source.Origin);
        Assert.Equal(fixture.Collection, referenceEdge.Source.Collection);
        Assert.Equal("qol", referenceEdge.Source.ReferenceModId);
        Assert.False(referenceEdge.Target.Resolved);
        Assert.Equal("UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", referenceEdge.Target.RawText);

        Assert.Equal(2, all.TotalCount);
        Assert.Equal(
            ["game-bound-callsite", "reference-latest-callsite"],
            all.Relationships.Select(edge => edge.RelationshipId));
        Assert.Contains(all.Relationships, edge => edge.Source.Origin == "game");
        Assert.Contains(all.Relationships, edge => edge.Source.Origin == "reference");
        Assert.DoesNotContain(all.Relationships, edge => edge.RelationshipId == "game-current-callsite");
        Assert.DoesNotContain(all.Relationships, edge => edge.RelationshipId == "reference-stale-callsite");
    }

    [Fact]
    public async Task ExplicitReferenceIndexSelection_UsesSelectedIndexForCallSitesAndFieldReferences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new FederatedIndexQueryService(_repository);

        var callSites = await service.CallSitesAsync(
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Reference, fixture.Collection),
            cancellationToken,
            fixture.StaleIndexId);
        var fieldReferences = await service.FieldReferencesAsync(
            "qol/Qol.Config.Setting",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Reference, fixture.Collection),
            FieldReferenceFilter.Writers,
            cancellationToken,
            fixture.StaleIndexId);

        Assert.Equal(["reference-stale-callsite"], callSites.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(["reference-field-write-stale"], fieldReferences.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(TargetRelationshipQueryNotices.FieldReferences, fieldReferences.CompletenessNotice);
    }

    [Fact]
    public async Task AllScopeReferenceFieldResolution_UsesPinnedReferenceIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new FederatedIndexQueryService(_repository);

        var result = await service.FieldReferencesAsync(
            "qol/Qol.Config.Setting",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.All, fixture.Collection),
            FieldReferenceFilter.Writers,
            cancellationToken,
            fixture.StaleIndexId);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(fixture.StaleIndexId, result.Resolution.Symbol!.IndexId);
        Assert.Equal(["reference-field-write-stale"], result.Relationships.Select(edge => edge.RelationshipId));
        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("reference", relationship.Source.Origin);
        Assert.Equal(fixture.Collection, relationship.Source.Collection);
        Assert.Equal("reference-field-writer-stale", relationship.Source.SymbolId);
        Assert.Equal(TargetRelationshipQueryNotices.FieldReferences, result.CompletenessNotice);
    }

    [Fact]
    public async Task FieldReferencesAsync_uses_scope_specific_completed_indexes_and_preserves_cross_origin_provenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedAsync(cancellationToken);
        var service = new FederatedIndexQueryService(_repository);

        var game = await service.FieldReferencesAsync(
            "Demo.State.Value",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Game),
            FieldReferenceFilter.All,
            cancellationToken);
        var reference = await service.FieldReferencesAsync(
            "qol/Qol.Config.Setting",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.Reference, fixture.Collection),
            FieldReferenceFilter.Writers,
            cancellationToken);
        var all = await service.FieldReferencesAsync(
            "Demo.State.Value",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 10, IndexQueryScope.All, fixture.Collection),
            FieldReferenceFilter.Readers,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, game.Resolution.Status);
        Assert.Equal(["game-current-field-read", "game-current-field-write"], game.Relationships.Select(edge => edge.RelationshipId));
        Assert.All(game.Relationships, edge => Assert.Equal("game", edge.Source.Origin));

        Assert.Equal(SymbolResolutionStatus.Resolved, reference.Resolution.Status);
        Assert.Equal("reference", reference.Resolution.Symbol!.Origin);
        Assert.Equal(fixture.Collection, reference.Resolution.Symbol.Collection);
        Assert.Equal(["reference-field-write-latest"], reference.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(TargetRelationshipQueryNotices.FieldReferences, reference.CompletenessNotice);
        var referenceFieldEdge = Assert.Single(reference.Relationships);
        Assert.Equal("reference", referenceFieldEdge.Source.Origin);
        Assert.Equal(fixture.Collection, referenceFieldEdge.Source.Collection);
        Assert.Equal("qol", referenceFieldEdge.Source.ReferenceModId);
        Assert.Equal("reference", referenceFieldEdge.Target.Origin);
        Assert.Equal(fixture.Collection, referenceFieldEdge.Target.Collection);

        Assert.Equal(SymbolResolutionStatus.Resolved, all.Resolution.Status);
        Assert.Equal("game", all.Resolution.Symbol!.Origin);
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(["game-bound-field-read", "reference-game-field-read"], all.Relationships.Select(edge => edge.RelationshipId));
        Assert.Contains(all.Relationships, edge => edge.Source.Origin == "game");
        Assert.Contains(all.Relationships, edge => edge.Source.Origin == "reference" && edge.Target.Origin == "game");
        Assert.DoesNotContain(all.Relationships, edge => edge.RelationshipId == "game-current-field-read");
    }

    private async Task<FederatedFixture> SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var collection = "qol";
        const string buildId = "build-bound";
        await _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, DateTimeOffset.Parse("2026-08-28T11:00:00Z"), true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                DateTimeOffset.Parse("2026-08-28T11:00:00Z")),
            cancellationToken);
        var boundGameRun = await CreateGameRunAsync(
            "bound",
            "2026-08-28T12:00:00Z",
            "game-bound-callsite",
            "game-bound-field-read",
            "game-bound-field-write",
            cancellationToken);
        await CreateGameRunAsync(
            "current",
            "2026-08-28T13:00:00Z",
            "game-current-callsite",
            "game-current-field-read",
            "game-current-field-write",
            cancellationToken);
        await CreateReferenceRunAsync(
            "stale",
            collection,
            boundGameRun.IndexId,
            buildId,
            "reference-stale-callsite",
            cancellationToken,
            includeReferenceFieldRelationships: true);
        await CreateReferenceRunAsync(
            "latest",
            collection,
            boundGameRun.IndexId,
            buildId,
            "reference-latest-callsite",
            cancellationToken,
            includeReferenceFieldRelationships: true,
            includeGameFieldRead: true);
        return new FederatedFixture(collection, "index-reference-stale", "index-reference-latest");
    }

    private async Task<IndexRunRecord> CreateGameRunAsync(
        string suffix,
        string completedAtUtc,
        string callSiteRelationshipId,
        string fieldReadRelationshipId,
        string fieldWriteRelationshipId,
        CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-game-" + suffix,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "game-extraction-" + suffix,
            completedAtUtc);
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord(
            "index-game-" + suffix,
            snapshot.SnapshotId,
            IndexRunStatus.Running,
            completedAtUtc);
        await _repository.StartIndexRunAsync(run, cancellationToken);

        var gameCallSource = Method("game-call-source-" + suffix, snapshot.SnapshotId, "Demo.Callers." + suffix + ".Run");
        var gameFieldReader = Method("game-field-reader-" + suffix, snapshot.SnapshotId, "Demo.FieldReaders." + suffix + ".Read");
        var gameFieldWriter = Method("game-field-writer-" + suffix, snapshot.SnapshotId, "Demo.FieldWriters." + suffix + ".Write");
        var gameField = Field("game-field-" + suffix, snapshot.SnapshotId, "Demo.State.Value");

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [gameCallSource, gameFieldReader, gameFieldWriter, gameField],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        callSiteRelationshipId,
                        snapshot.SnapshotId,
                        gameCallSource.SymbolId,
                        null,
                        "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                        "Calls",
                        "fixture:game"),
                    new IndexRelationshipRecord(
                        fieldReadRelationshipId,
                        snapshot.SnapshotId,
                        gameFieldReader.SymbolId,
                        gameField.SymbolId,
                        "Demo.State::Value",
                        "ReadsField",
                        "fixture:game"),
                    new IndexRelationshipRecord(
                        fieldWriteRelationshipId,
                        snapshot.SnapshotId,
                        gameFieldWriter.SymbolId,
                        gameField.SymbolId,
                        "Demo.State::Value",
                        "WritesField",
                        "fixture:game")
                ]),
            completedAtUtc,
            cancellationToken);
        return run with { Status = IndexRunStatus.Completed, CompletedAtUtc = completedAtUtc };
    }

    private async Task CreateReferenceRunAsync(
        string suffix,
        string collection,
        string gameIndexId,
        string buildId,
        string callSiteRelationshipId,
        CancellationToken cancellationToken,
        bool includeReferenceFieldRelationships = false,
        bool includeGameFieldRead = false)
    {
        var createdAtUtc = suffix == "latest"
            ? "2026-08-28T15:00:00Z"
            : "2026-08-28T14:00:00Z";
        var completedAtUtc = suffix == "latest"
            ? "2026-08-28T15:01:00Z"
            : "2026-08-28T14:01:00Z";
        var snapshot = new CodeSnapshotRecord(
            "snapshot-reference-" + suffix,
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            collection,
            createdAtUtc);
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord(
            "index-reference-" + suffix,
            snapshot.SnapshotId,
            IndexRunStatus.Running,
            snapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(run, cancellationToken);

        var referenceCallSource = Method("reference-call-source-" + suffix, snapshot.SnapshotId, "qol/Qol.Caller." + suffix + ".Run");
        var referenceFieldReader = Method("reference-field-reader-" + suffix, snapshot.SnapshotId, "qol/Qol.FieldReader." + suffix + ".Read");
        var referenceFieldWriter = Method("reference-field-writer-" + suffix, snapshot.SnapshotId, "qol/Qol.FieldWriter." + suffix + ".Write");
        var referenceField = Field("reference-field-" + suffix, snapshot.SnapshotId, "qol/Qol.Config.Setting");
        var symbols = new List<IndexSymbolRecord> { referenceCallSource, referenceFieldReader, referenceFieldWriter, referenceField };
        var relationships = new List<IndexRelationshipRecord>
        {
            new(
                callSiteRelationshipId,
                snapshot.SnapshotId,
                referenceCallSource.SymbolId,
                null,
                "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
                "Calls",
                "fixture:reference")
        };

        if (includeGameFieldRead)
        {
            relationships.Add(
                new IndexRelationshipRecord(
                    "reference-game-field-read",
                    snapshot.SnapshotId,
                    referenceFieldReader.SymbolId,
                    "game-field-bound",
                    "Demo.State::Value",
                    "ReadsField",
                    "fixture:reference"));
        }

        if (includeReferenceFieldRelationships)
        {
            relationships.Add(
                new IndexRelationshipRecord(
                    "reference-field-read-" + suffix,
                    snapshot.SnapshotId,
                    referenceFieldReader.SymbolId,
                    referenceField.SymbolId,
                    "qol/Qol.Config::Setting",
                    "ReadsField",
                    "fixture:reference"));
            relationships.Add(
                new IndexRelationshipRecord(
                    "reference-field-write-" + suffix,
                    snapshot.SnapshotId,
                    referenceFieldWriter.SymbolId,
                    referenceField.SymbolId,
                    "qol/Qol.Config::Setting",
                    "WritesField",
                    "fixture:reference"));
        }

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                relationships,
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        "mods/qol",
                        "qol-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            completedAtUtc,
            cancellationToken);
    }

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "ReferenceOrGame:Installed:Method:" + CanonicalMember(qualifiedName),
            "Method",
            qualifiedName,
            "System.Void " + CanonicalMember(qualifiedName) + "()",
            false,
            BodyRecoveryStatus.Recovered);

    private static IndexSymbolRecord Field(string id, string snapshotId, string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        var typeName = qualifiedName[..separator];
        var fieldName = qualifiedName[(separator + 1)..];
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ReferenceOrGame:Installed:Field:" + typeName + "::System.Int32 " + fieldName,
            "Field",
            qualifiedName,
            "System.Int32 " + typeName + "::" + fieldName,
            false);
    }

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record FederatedFixture(string Collection, string StaleIndexId, string LatestIndexId);
}
