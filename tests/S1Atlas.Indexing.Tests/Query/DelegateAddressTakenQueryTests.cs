using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class DelegateAddressTakenQueryTests : IAsyncDisposable
{
    private const string Collection = "delegate-collection";
    private const string Caller = "Demo.Call::Run():System.Void";
    private const string Creator = "Demo.Create::Build():System.Void";
    private const string Creator2 = "Demo.Create2::Build():System.Void";
    private const string MetaCreator = "Demo.Meta::Build():System.Void";
    private const string Target = "Demo.Target::Handle():System.Void";
    private const string Leaf = "Demo.Leaf::Help():System.Int32";
    private const string Lambda = "Demo.Create2+<>c::<Build>b__0_0():System.Void";
    private const string UserField = "Demo.State::count";
    private const string Reader = "Demo.Use::Read():System.Void";
    private const string Writer = "Demo.Use::Write():System.Void";
    private const string Taker = "Demo.Use::Take():System.Void";
    private const string TokenTaker = "Demo.Use::TakeToken():System.Void";
    private const string RefCreator = "Demo.RefCreate::Build():System.Void";
    private const string RefTarget = "Demo.RefTarget::Handle():System.Void";

    private const string DelegateLabel = "delegate created (not called)";
    private const string MetadataMethodLabel = "metadata reference (not called)";
    private const string PossibleWriteLabel = "possible write (address taken)";
    private const string PossibleReadLabel = "possible read (address taken)";
    private const string MetadataFieldLabel = "metadata reference (not read)";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-delegate-query-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Callers_exclude_delegate_creation_by_default()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var hidden = await service.CallersAsync(Target, options, TestContext.Current.CancellationToken);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Empty(hidden.Relationships);

        var normal = await service.CallersAsync(Leaf, options, TestContext.Current.CancellationToken);
        Assert.Equal(Caller, Assert.Single(normal.Relationships).Source.QualifiedName);
    }

    [Fact]
    public async Task Callers_include_delegates_when_asked_with_label()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var shown = await service.CallersAsync(Target, options, TestContext.Current.CancellationToken, includeDelegates: true);

        Assert.Equal(2, shown.TotalCount);
        Assert.All(shown.Relationships, row => Assert.Equal(DelegateLabel, row.Label));
        Assert.Contains(shown.Relationships, row => row.Source.QualifiedName == Creator);
        Assert.Contains(shown.Relationships, row => row.Source.QualifiedName == Creator2);
    }

    [Fact]
    public async Task Callees_exclude_delegate_creation_by_default()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var hidden = await service.CalleesAsync(Creator, options, TestContext.Current.CancellationToken);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Empty(hidden.Relationships);
    }

    [Fact]
    public async Task Callees_include_delegates_when_asked_with_label()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var shown = await service.CalleesAsync(Creator, options, TestContext.Current.CancellationToken, includeDelegates: true);

        var row = Assert.Single(shown.Relationships);
        Assert.Equal(Target, row.Target.QualifiedName);
        Assert.Equal(DelegateLabel, row.Label);
    }

    [Fact]
    public async Task Fieldrefs_readers_and_writers_both_show_address_taken_with_labels()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var readers = await service.FieldReferencesAsync(
            UserField, options, FieldReferenceFilter.Readers, TestContext.Current.CancellationToken);
        Assert.Equal(2, readers.TotalCount);
        var readRow = Assert.Single(readers.Page.Relationships, row => row.Source.QualifiedName == Taker);
        Assert.Equal(PossibleReadLabel, readRow.Label);

        var writers = await service.FieldReferencesAsync(
            UserField, options, FieldReferenceFilter.Writers, TestContext.Current.CancellationToken);
        Assert.Equal(2, writers.TotalCount);
        var writeRow = Assert.Single(writers.Page.Relationships, row => row.Source.QualifiedName == Taker);
        Assert.Equal(PossibleWriteLabel, writeRow.Label);
    }

    [Fact]
    public async Task Fieldrefs_all_shows_address_taken_once_with_write_label()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var all = await service.FieldReferencesAsync(
            UserField, options, FieldReferenceFilter.All, TestContext.Current.CancellationToken);

        Assert.Equal(3, all.TotalCount);
        var addressRow = Assert.Single(all.Page.Relationships, row => row.Source.QualifiedName == Taker);
        Assert.Equal(PossibleWriteLabel, addressRow.Label);
    }

    [Fact]
    public async Task Refs_always_shows_new_kinds_with_labels()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var targetRefs = await service.RefsAsync(Target, options, TestContext.Current.CancellationToken);
        Assert.Equal(3, targetRefs.TotalCount);
        Assert.All(
            targetRefs.Relationships.Where(row => row.Source.QualifiedName != MetaCreator),
            row => Assert.Equal(DelegateLabel, row.Label));
        var metaRow = Assert.Single(targetRefs.Relationships, row => row.Source.QualifiedName == MetaCreator);
        Assert.Equal(MetadataMethodLabel, metaRow.Label);

        var fieldRefs = await service.RefsAsync(UserField, options, TestContext.Current.CancellationToken);
        Assert.Equal(4, fieldRefs.TotalCount);
        var addressRow = Assert.Single(fieldRefs.Relationships, row => row.Source.QualifiedName == Taker);
        Assert.Equal(PossibleWriteLabel, addressRow.Label);
        var tokenRow = Assert.Single(fieldRefs.Relationships, row => row.Source.QualifiedName == TokenTaker);
        Assert.Equal(MetadataFieldLabel, tokenRow.Label);
    }

    [Fact]
    public async Task Metadata_ldtoken_rows_stay_out_of_callers_and_field_references()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var callers = await service.CallersAsync(Target, options, TestContext.Current.CancellationToken, includeDelegates: true);
        Assert.DoesNotContain(callers.Relationships, row => row.Source.QualifiedName == MetaCreator);

        var fieldReferences = await service.FieldReferencesAsync(
            UserField, options, FieldReferenceFilter.All, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fieldReferences.Page.Relationships, row => row.Source.QualifiedName == TokenTaker);
    }

    [Fact]
    public async Task Delegate_created_inside_lambda_is_credited_with_in_lambda()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var shown = await service.CallersAsync(Target, options, TestContext.Current.CancellationToken, includeDelegates: true);

        var row = Assert.Single(shown.Relationships, item => item.Source.QualifiedName == Creator2);
        Assert.Equal("in lambda", row.GeneratedDetail);
        Assert.Equal(DelegateLabel, row.Label);
    }

    [Fact]
    public async Task ReferenceScope_DelegatesHiddenByDefaultAndShownWhenAsked()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, Collection);

        var hidden = await service.CallersAsync(RefTarget, options, TestContext.Current.CancellationToken);
        Assert.Empty(hidden.Relationships);

        var shown = await service.CallersAsync(RefTarget, options, TestContext.Current.CancellationToken, includeDelegates: true);
        var row = Assert.Single(shown.Relationships);
        Assert.Equal(RefCreator, row.Source.QualifiedName);
        Assert.Equal(DelegateLabel, row.Label);
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
        const string buildId = "build-delegate";
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
        return repository;
    }

    private static async Task<string> SeedGameRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-delegate-game", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-delegate-game", "2026-09-01T12:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-delegate-game", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T12:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var caller = Method("symbol-caller", snapshot.SnapshotId, Caller);
        var creator = Method("symbol-creator", snapshot.SnapshotId, Creator);
        var creator2 = Method("symbol-creator2", snapshot.SnapshotId, Creator2);
        var metaCreator = Method("symbol-meta-creator", snapshot.SnapshotId, MetaCreator);
        var target = Method("symbol-target", snapshot.SnapshotId, Target);
        var leaf = Method("symbol-leaf", snapshot.SnapshotId, Leaf);
        var lambda = Method("symbol-lambda", snapshot.SnapshotId, Lambda, isGenerated: true);
        var field = Field("symbol-field", snapshot.SnapshotId, UserField);
        var reader = Method("symbol-reader", snapshot.SnapshotId, Reader);
        var writer = Method("symbol-writer", snapshot.SnapshotId, Writer);
        var taker = Method("symbol-taker", snapshot.SnapshotId, Taker);
        var tokenTaker = Method("symbol-token-taker", snapshot.SnapshotId, TokenTaker);
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [caller, creator, creator2, metaCreator, target, leaf, lambda, field, reader, writer, taker, tokenTaker],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-call", snapshot.SnapshotId, caller.SymbolId, leaf.SymbolId,
                        leaf.QualifiedName, "Calls", "RecoveredIL"),
                    new IndexRelationshipRecord(
                        "rel-delegate", snapshot.SnapshotId, creator.SymbolId, target.SymbolId,
                        target.QualifiedName, "ReferencesMethod", "RecoveredIL"),
                    new IndexRelationshipRecord(
                        "rel-delegate-meta", snapshot.SnapshotId, metaCreator.SymbolId, target.SymbolId,
                        target.QualifiedName, "ReferencesMethod", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-delegate-credited", snapshot.SnapshotId, creator2.SymbolId, target.SymbolId,
                        target.QualifiedName, "ReferencesMethod", "RecoveredIL",
                        GeneratedSourceSymbolId: lambda.SymbolId, GeneratedDetail: "in lambda"),
                    new IndexRelationshipRecord(
                        "rel-read", snapshot.SnapshotId, reader.SymbolId, field.SymbolId,
                        field.QualifiedName, "ReadsField", "RecoveredIL"),
                    new IndexRelationshipRecord(
                        "rel-write", snapshot.SnapshotId, writer.SymbolId, field.SymbolId,
                        field.QualifiedName, "WritesField", "RecoveredIL"),
                    new IndexRelationshipRecord(
                        "rel-address", snapshot.SnapshotId, taker.SymbolId, field.SymbolId,
                        field.QualifiedName, "TakesFieldAddress", "RecoveredIL"),
                    new IndexRelationshipRecord(
                        "rel-address-meta", snapshot.SnapshotId, tokenTaker.SymbolId, field.SymbolId,
                        field.QualifiedName, "TakesFieldAddress", "Metadata")
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
            "snapshot-delegate-reference", CodebaseKind.ReferenceMod, CodeChannel.Installed,
            Collection, "2026-09-01T13:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-delegate-reference", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await repository.StartIndexRunAsync(run, cancellationToken);
        var creator = Method("symbol-ref-creator", snapshot.SnapshotId, RefCreator, CodebaseKind.ReferenceMod);
        var target = Method("symbol-ref-target", snapshot.SnapshotId, RefTarget, CodebaseKind.ReferenceMod);
        var symbols = new List<IndexSymbolRecord> { creator, target };
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-ref-delegate", snapshot.SnapshotId, creator.SymbolId, target.SymbolId,
                        target.QualifiedName, "ReferencesMethod", "RecoveredIL")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        Collection,
                        "Delegate",
                        "1.0.0",
                        "MIT",
                        "mods/delegate",
                        "delegate-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            "2026-09-01T13:01:00Z",
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
        string qualifiedName) =>
        new(
            id,
            snapshotId,
            CodebaseKind.ScheduleI + ":" + CodeChannel.Installed + ":Field:" + qualifiedName,
            "Field",
            qualifiedName,
            "System.Int32 " + qualifiedName,
            false,
            BodyRecoveryStatus.Recovered);
}
