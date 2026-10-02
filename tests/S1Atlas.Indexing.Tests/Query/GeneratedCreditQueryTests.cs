using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class GeneratedCreditQueryTests : IAsyncDisposable
{
    private const string Collection = "credit-collection";
    private const string GameFoo = "Demo.Foo::Run():System.Void";
    private const string GameLambda = "Demo.Foo+<>c::<Run>b__0_0():System.Void";
    private const string GameLeaf = "Demo.Leaf::Help():System.Int32";
    private const string GameLeaf2 = "Demo.Leaf2::Help():System.Int32";
    private const string GameOther = "Demo.Other::M():System.Void";
    private const string GameStray = "Demo.Multi+<>c::<Foo>b__0_0():System.Void";
    private const string GameGenField = "Demo.Widget+<>c__DisplayClass0_0::x";
    private const string GameUserField = "Demo.Widget::count";
    private const string GameDispatchBase = "Demo.Dispatch.Base::Execute():System.Void";
    private const string GameDispatchOverride = "Demo.Dispatch.Derived::Execute():System.Void";
    private const string GameDispatchCaller = "Demo.Dispatch.Caller::Run():System.Void";
    private const string GameDispatchLambda = "Demo.Dispatch.Caller+<>c::<Run>b__0_0():System.Void";
    private const string RefFoo = "Demo.RefFoo::Run():System.Void";
    private const string RefLambda = "Demo.RefFoo+<>c::<Run>b__0_0():System.Void";
    private const string RefLeaf = "Demo.RefLeaf::Help():System.Int32";
    private const string ApiFoo = "Demo.ApiFoo::Run():System.Void";
    private const string ApiLambda = "Demo.ApiFoo+<>c::<Run>b__0_0():System.Void";
    private const string ApiLeaf = "Demo.ApiLeaf::Help():System.Int32";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-generated-credit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GameScope_DefaultShowsCreditedCallersWithDetails()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameLeaf,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        var byCaller = result.Relationships.ToDictionary(edge => edge.Source.QualifiedName!, StringComparer.Ordinal);
        Assert.Equal("in lambda", byCaller[GameFoo].GeneratedDetail);
        Assert.Null(byCaller[GameOther].GeneratedDetail);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", byCaller[GameStray].GeneratedDetail);
        Assert.DoesNotContain(result.Relationships, edge => edge.Source.QualifiedName == GameLambda);
    }

    [Fact]
    public async Task GameScope_CalleesIncludeBodyCallsWithDetails()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CalleesAsync(
            GameFoo,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken);

        var byTarget = result.Relationships.ToDictionary(edge => edge.Target.QualifiedName!, StringComparer.Ordinal);
        Assert.Equal("in lambda", byTarget[GameLeaf].GeneratedDetail);
        Assert.Null(byTarget[GameLeaf2].GeneratedDetail);
    }

    [Fact]
    public async Task GameScope_IncludeGeneratedShowsRawSources()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameLeaf,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken,
            includeGenerated: true);

        var byCaller = result.Relationships.ToDictionary(edge => edge.Source.QualifiedName!, StringComparer.Ordinal);
        Assert.Null(byCaller[GameLambda].GeneratedDetail);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", byCaller[GameStray].GeneratedDetail);
        Assert.DoesNotContain(result.Relationships, edge => edge.Source.QualifiedName == GameFoo);
    }

    [Fact]
    public async Task GameScope_CreditedEdgeKeepsOneIdAcrossModes()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var credited = await service.CallersAsync(GameLeaf, options, TestContext.Current.CancellationToken);
        var raw = await service.CallersAsync(GameLeaf, options, TestContext.Current.CancellationToken, includeGenerated: true);

        var creditedRow = Assert.Single(credited.Relationships, edge => edge.Source.QualifiedName == GameFoo);
        var rawRow = Assert.Single(raw.Relationships, edge => edge.Source.QualifiedName == GameLambda);
        Assert.Equal(creditedRow.RelationshipId, rawRow.RelationshipId);
    }

    [Fact]
    public async Task ReferenceScope_DefaultAndIncludeGenerated()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, Collection);

        var credited = await service.CallersAsync(RefLeaf, options, TestContext.Current.CancellationToken);
        var creditedRow = Assert.Single(credited.Relationships);
        Assert.Equal(RefFoo, creditedRow.Source.QualifiedName);
        Assert.Equal("in lambda", creditedRow.GeneratedDetail);

        var raw = await service.CallersAsync(RefLeaf, options, TestContext.Current.CancellationToken, includeGenerated: true);
        var rawRow = Assert.Single(raw.Relationships);
        Assert.Equal(RefLambda, rawRow.Source.QualifiedName);
        Assert.Null(rawRow.GeneratedDetail);
    }

    [Fact]
    public async Task AllScope_CarriesDetailsThroughMerge()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameLeaf,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken);

        var byCaller = result.Relationships.ToDictionary(edge => edge.Source.QualifiedName!, StringComparer.Ordinal);
        Assert.Equal("in lambda", byCaller[GameFoo].GeneratedDetail);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", byCaller[GameStray].GeneratedDetail);
    }

    [Fact]
    public async Task ApiRelationshipsSelected_DefaultAndIncludeGenerated()
    {
        var repository = await SeedRepositoryAsync(TestContext.Current.CancellationToken);
        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository));
        var selection = new ApiIndexSelection(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            ApiIndexAvailability.Current,
            "index-credit-api",
            "snapshot-credit-api",
            "api-source",
            null,
            "current");

        var credited = await service.RelationshipsSelectedAsync(
            selection, ApiLeaf, 50, ApiRelationshipDirection.Callers, null,
            TestContext.Current.CancellationToken);
        var creditedRow = Assert.Single(credited.Relationships);
        Assert.Equal(ApiFoo, creditedRow.Source.QualifiedName);
        Assert.Equal("in lambda", creditedRow.GeneratedDetail);

        var raw = await service.RelationshipsSelectedAsync(
            selection, ApiLeaf, 50, ApiRelationshipDirection.Callers, null,
            TestContext.Current.CancellationToken,
            includeGenerated: true);
        var rawRow = Assert.Single(raw.Relationships);
        Assert.Equal(ApiLambda, rawRow.Source.QualifiedName);
        Assert.Null(rawRow.GeneratedDetail);
    }

    [Fact]
    public async Task GameScope_SearchHidesGeneratedWithNotice()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.SearchAsync(
            "Foo",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(GameFoo, Assert.Single(result.Results).QualifiedName);
        Assert.Equal(
            "2 generated result(s) hidden. Re-run with includeGenerated to include them.",
            result.SearchNotice);
    }

    [Fact]
    public async Task GameScope_SearchIncludeGeneratedShowsAll()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.SearchAsync(
            "Foo",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken,
            includeGenerated: true);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            [GameLambda, GameFoo, GameStray],
            result.Results.Select(row => row.QualifiedName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Null(result.SearchNotice);
    }

    [Fact]
    public async Task ReferenceScope_SearchHidesGeneratedWithNotice()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.SearchAsync(
            "RefFoo",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(RefFoo, Assert.Single(result.Results).QualifiedName);
        Assert.Equal(
            "1 generated result(s) hidden. Re-run with includeGenerated to include them.",
            result.SearchNotice);
    }

    [Fact]
    public async Task AllScope_SearchMergesHiddenCounts()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.SearchAsync(
            "Foo",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(
            [GameFoo, RefFoo],
            result.Results.Select(row => row.QualifiedName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            "3 generated result(s) hidden. Re-run with includeGenerated to include them.",
            result.SearchNotice);
    }

    [Fact]
    public async Task ApiSearchSelected_HidesGeneratedWithNotice()
    {
        var repository = await SeedRepositoryAsync(TestContext.Current.CancellationToken);
        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository));
        var selection = new ApiIndexSelection(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            ApiIndexAvailability.Current,
            "index-credit-api",
            "snapshot-credit-api",
            "api-source",
            null,
            "current");

        var hidden = await service.SearchSelectedAsync(
            selection, "ApiFoo", 50, TestContext.Current.CancellationToken);
        Assert.Equal(1, hidden.TotalCount);
        Assert.Equal(ApiFoo, Assert.Single(hidden.Results).QualifiedName);
        Assert.Equal(
            "1 generated result(s) hidden. Re-run with includeGenerated to include them.",
            hidden.SearchNotice);

        var raw = await service.SearchSelectedAsync(
            selection, "ApiFoo", 50, TestContext.Current.CancellationToken, includeGenerated: true);
        Assert.Equal(2, raw.TotalCount);
        Assert.Null(raw.SearchNotice);
    }

    [Fact]
    public async Task GameScope_RankedSearchHidesGeneratedWithNotice()
    {
        var repository = await SeedRepositoryAsync(TestContext.Current.CancellationToken);
        var run = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, null, TestContext.Current.CancellationToken);
        var service = new IndexQueryService(repository);

        var hidden = await service.SearchRankedInIndexAsync(
            run!,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Foo",
            50,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, hidden.TotalCount);
        Assert.Equal(GameFoo, Assert.Single(hidden.Results).QualifiedName);
        Assert.Equal(
            "2 generated result(s) hidden. Re-run with includeGenerated to include them.",
            hidden.SearchNotice);

        var raw = await service.SearchRankedInIndexAsync(
            run!,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Foo",
            50,
            null,
            TestContext.Current.CancellationToken,
            includeGenerated: true);
        Assert.Equal(3, raw.TotalCount);
        Assert.Null(raw.SearchNotice);
    }

    [Fact]
    public async Task GameScope_FieldReferencesOfGeneratedFieldEmptyByDefault()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var hidden = await service.FieldReferencesAsync(
            GameGenField, options, FieldReferenceFilter.All, TestContext.Current.CancellationToken);
        Assert.Equal(SymbolResolutionStatus.Resolved, hidden.Resolution.Status);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Empty(hidden.Page.Relationships);

        var raw = await service.FieldReferencesAsync(
            GameGenField, options, FieldReferenceFilter.All, TestContext.Current.CancellationToken, includeGenerated: true);
        var row = Assert.Single(raw.Page.Relationships);
        Assert.Equal(GameFoo, row.Source.QualifiedName);
    }

    [Fact]
    public async Task GameScope_DispatchDerivedRowCarriesGeneratedDetail()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var combined = await service.CallersAsync(
            GameDispatchOverride, options, TestContext.Current.CancellationToken);
        var row = Assert.Single(combined.Relationships);
        Assert.Equal(GameDispatchCaller, row.Source.QualifiedName);
        Assert.Equal("in lambda", row.GeneratedDetail);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {GameDispatchBase}"], row.Routes);

        var exact = await service.CallersAsync(
            GameDispatchOverride, options, TestContext.Current.CancellationToken, exact: true);
        Assert.Equal(0, exact.TotalCount);
        Assert.Empty(exact.Relationships);

        var raw = await service.CallersAsync(
            GameDispatchOverride, options, TestContext.Current.CancellationToken, includeGenerated: true);
        var rawRow = Assert.Single(raw.Relationships);
        Assert.Equal(GameDispatchLambda, rawRow.Source.QualifiedName);
        Assert.Null(rawRow.GeneratedDetail);
        Assert.True(rawRow.IsDerived);
        Assert.Equal([$"via {GameDispatchBase}"], rawRow.Routes);
    }

    [Fact]
    public async Task GameScope_FieldReferencesCreditLambdaReaders()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var credited = await service.FieldReferencesAsync(
            GameUserField, options, FieldReferenceFilter.Readers, TestContext.Current.CancellationToken);
        var creditedRow = Assert.Single(credited.Page.Relationships);
        Assert.Equal(GameFoo, creditedRow.Source.QualifiedName);
        Assert.Equal("in lambda", creditedRow.GeneratedDetail);

        var raw = await service.FieldReferencesAsync(
            GameUserField, options, FieldReferenceFilter.Readers, TestContext.Current.CancellationToken, includeGenerated: true);
        var rawRow = Assert.Single(raw.Page.Relationships);
        Assert.Equal(GameLambda, rawRow.Source.QualifiedName);
        Assert.Null(rawRow.GeneratedDetail);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task<FederatedIndexQueryService> SeedAsync(CancellationToken cancellationToken)
    {
        var repository = await SeedRepositoryAsync(cancellationToken);
        return new FederatedIndexQueryService(repository);
    }

    private async Task<SqliteAtlasRepository> SeedRepositoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        const string buildId = "build-credit";
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, DateTimeOffset.Parse("2026-09-01T11:00:00Z"), true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                DateTimeOffset.Parse("2026-09-01T11:00:00Z")),
            cancellationToken);
        var gameIndexId = await SeedGameRunAsync(repository, cancellationToken);
        await SeedReferenceRunAsync(repository, gameIndexId, buildId, cancellationToken);
        await SeedApiRunAsync(repository, cancellationToken);
        return repository;
    }

    private static async Task<string> SeedGameRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-credit-game", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-credit-game", "2026-09-01T12:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-credit-game", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T12:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var foo = Method("symbol-foo", snapshot.SnapshotId, GameFoo);
        var lambda = Method("symbol-lambda", snapshot.SnapshotId, GameLambda, isGenerated: true);
        var leaf = Method("symbol-leaf", snapshot.SnapshotId, GameLeaf);
        var leaf2 = Method("symbol-leaf2", snapshot.SnapshotId, GameLeaf2);
        var other = Method("symbol-other", snapshot.SnapshotId, GameOther);
        var stray = Method("symbol-stray", snapshot.SnapshotId, GameStray, isGenerated: true);
        var genField = Field("symbol-widget-capture", snapshot.SnapshotId, GameGenField, isGenerated: true);
        var userField = Field("symbol-widget-count", snapshot.SnapshotId, GameUserField);
        var dispatchBase = Method("symbol-dispatch-base", snapshot.SnapshotId, GameDispatchBase);
        var dispatchOverride = Method("symbol-dispatch-override", snapshot.SnapshotId, GameDispatchOverride);
        var dispatchCaller = Method("symbol-dispatch-caller", snapshot.SnapshotId, GameDispatchCaller);
        var dispatchLambda = Method("symbol-dispatch-lambda", snapshot.SnapshotId, GameDispatchLambda, isGenerated: true);
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [foo, lambda, leaf, leaf2, other, stray, genField, userField, dispatchBase, dispatchOverride, dispatchCaller, dispatchLambda],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-credited", snapshot.SnapshotId, foo.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "Body",
                        GeneratedSourceSymbolId: lambda.SymbolId, GeneratedDetail: "in lambda"),
                    new IndexRelationshipRecord(
                        "rel-normal", snapshot.SnapshotId, other.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "Body"),
                    new IndexRelationshipRecord(
                        "rel-stray", snapshot.SnapshotId, stray.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "Body",
                        GeneratedDetail: "unmapped: ambiguous overloads sharing 'Foo'"),
                    new IndexRelationshipRecord(
                        "rel-foo-direct", snapshot.SnapshotId, foo.SymbolId, leaf2.SymbolId,
                        leaf2.QualifiedName, "Calls", "Body"),
                    new IndexRelationshipRecord(
                        "rel-widget-read", snapshot.SnapshotId, foo.SymbolId, genField.SymbolId,
                        genField.QualifiedName, "ReadsField", "Body"),
                    new IndexRelationshipRecord(
                        "rel-count-read", snapshot.SnapshotId, foo.SymbolId, userField.SymbolId,
                        userField.QualifiedName, "ReadsField", "Body",
                        GeneratedSourceSymbolId: lambda.SymbolId, GeneratedDetail: "in lambda"),
                    new IndexRelationshipRecord(
                        "rel-dispatch-override", snapshot.SnapshotId, dispatchOverride.SymbolId, dispatchBase.SymbolId,
                        dispatchBase.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-dispatch-call", snapshot.SnapshotId, dispatchCaller.SymbolId, dispatchBase.SymbolId,
                        dispatchBase.QualifiedName, "CallsVirtual", "Body",
                        GeneratedSourceSymbolId: dispatchLambda.SymbolId, GeneratedDetail: "in lambda")
                ]),
            "2026-09-01T12:00:00Z",
            cancellationToken);
        return run.IndexId;
    }

    private static async Task SeedReferenceRunAsync(
        SqliteAtlasRepository repository,
        string gameIndexId,
        string buildId,
        CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-credit-reference", CodebaseKind.ReferenceMod, CodeChannel.Installed,
            Collection, "2026-09-01T13:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-credit-reference", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await repository.StartIndexRunAsync(run, cancellationToken);
        var foo = Method("symbol-ref-foo", snapshot.SnapshotId, RefFoo, CodebaseKind.ReferenceMod);
        var lambda = Method("symbol-ref-lambda", snapshot.SnapshotId, RefLambda, CodebaseKind.ReferenceMod, isGenerated: true);
        var leaf = Method("symbol-ref-leaf", snapshot.SnapshotId, RefLeaf, CodebaseKind.ReferenceMod);
        var symbols = new List<IndexSymbolRecord> { foo, lambda, leaf };
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-ref-credited", snapshot.SnapshotId, foo.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "Body",
                        GeneratedSourceSymbolId: lambda.SymbolId, GeneratedDetail: "in lambda")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        Collection,
                        "Credit",
                        "1.0.0",
                        "MIT",
                        "mods/credit",
                        "credit-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            "2026-09-01T13:01:00Z",
            cancellationToken);
    }

    private static async Task SeedApiRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-credit-api", CodebaseKind.S1Api, CodeChannel.Installed,
            "extraction-credit-api", "2026-09-01T14:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-credit-api", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T14:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var foo = Method("symbol-api-foo", snapshot.SnapshotId, ApiFoo, CodebaseKind.S1Api);
        var lambda = Method("symbol-api-lambda", snapshot.SnapshotId, ApiLambda, CodebaseKind.S1Api, isGenerated: true);
        var leaf = Method("symbol-api-leaf", snapshot.SnapshotId, ApiLeaf, CodebaseKind.S1Api);
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [foo, lambda, leaf],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-api-credited", snapshot.SnapshotId, foo.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "Body",
                        GeneratedSourceSymbolId: lambda.SymbolId, GeneratedDetail: "in lambda")
                ]),
            "2026-09-01T14:01:00Z",
            cancellationToken);
    }

    private static IndexSymbolRecord Method(
        string id,
        string snapshotId,
        string qualifiedName,
        CodebaseKind codebase = CodebaseKind.ScheduleI,
        CodeChannel channel = CodeChannel.Installed,
        bool isGenerated = false) =>
        new(
            id,
            snapshotId,
            codebase + ":" + channel + ":Method:" + qualifiedName,
            "Method",
            qualifiedName,
            "System.Void " + qualifiedName,
            false,
            BodyRecoveryStatus.Recovered,
            IsGenerated: isGenerated);

    private static IndexSymbolRecord Field(
        string id,
        string snapshotId,
        string qualifiedName,
        bool isGenerated = false) =>
        new(
            id,
            snapshotId,
            CodebaseKind.ScheduleI + ":" + CodeChannel.Installed + ":Field:" + qualifiedName,
            "Field",
            qualifiedName,
            "System.Int32 " + qualifiedName,
            false,
            BodyRecoveryStatus.Recovered,
            IsGenerated: isGenerated);
}
