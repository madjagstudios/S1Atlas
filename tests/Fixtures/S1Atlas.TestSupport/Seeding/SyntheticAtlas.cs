using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using System.Globalization;
using System.Text;

namespace S1Atlas.TestSupport.Seeding;

// A small synthetic atlas for serve tests: one healthy installed build with a
// completed Schedule I index (a Widget graph, thirty paged types, and one
// hostile-named type) plus a completed S1API Release index. All names and
// bytes are fake; nothing here touches the game or the network.
public sealed class SyntheticAtlas : IAsyncDisposable
{
    public const string BuildIdValue = "build-serve-1";
    public const string GameIndexIdValue = "index-serve-game";
    public const string ApiIndexIdValue = "index-serve-api";
    public const string ApiCommitShaValue = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public const string WidgetTypeId = "type-serve-widget";
    public const string RunMethodId = "method-serve-run";
    public const string CheckPhysicsMethodId = "method-serve-check-physics";
    public const string StateFieldId = "field-serve-state";
    public const string CallerMethodId = "method-serve-caller";
    public const string ExecuteMethodId = "method-serve-execute";
    public const string BaseTypeId = "type-serve-base";
    public const string BaseRenderMethodId = "method-serve-baserender";
    public const string RenderMethodId = "method-serve-render";
    public const string PayloadTypeId = "type-serve-payload";
    public const string ResultTypeId = "type-serve-result";
    public const string HostileTypeId = "type-serve-hostile";
    public const string HostileQualifiedName = "Evil.<img src=x onerror=alert(1)>";

    public const string CreditFooMethodId = "method-serve-credit-foo";
    public const string CreditLambdaMethodId = "method-serve-credit-lambda";
    public const string CreditLeafMethodId = "method-serve-credit-leaf";
    public const string CreditCaptureFieldId = "field-serve-credit-capture";

    public const string CatalogTypeId = "type-serve-catalog";
    public const string LookupMethodId = "method-serve-lookup";
    public const string AssistMethodId = "method-serve-assist";

    public const string TypeSelector = "Demo.Widget";
    public const string RunSelector = "System.Void Demo.Widget::Run()";
    public const string LookupSelector = "System.Void ServeApi.Catalog::Lookup()";
    public const string TurboQualifiedName = "Demo.Widget.Turbo";

    public const string BuildIdAValue = "build-serve-a";
    public const string BuildIdBValue = "build-serve-b";
    public const string GameIndexAValue = "index-serve-a";
    public const string GameIndexBValue = "index-serve-b";

    public const string LeakRootToken = "servefake-zqxv-9182";
    public const string LeakOutsideToken = "servefake-elsewhere-5511";
    public const string LeakInstallationRoot = "C:\\servefake-zqxv-9182\\game";

    public const string GameSourceText =
        "namespace Demo;\n" +
        "public class WidgetBase\n" +
        "{\n" +
        "}\n" +
        "public class Widget : WidgetBase\n" +
        "{\n" +
        "    private int _state;\n" +
        "    public void Run() { }\n" +
        "    public void CheckPhysics() { Physics.IgnoreLayerCollision(0, 1); }\n" +
        "}\n" +
        "public class Caller\n" +
        "{\n" +
        "    public void Invoke() { }\n" +
        "}\n" +
        "public class Service\n" +
        "{\n" +
        "    public void Execute() { }\n" +
        "}\n" +
        "public class Payload\n" +
        "{\n" +
        "}\n" +
        "public class Result\n" +
        "{\n" +
        "}\n";

    public const string HostileSourceText =
        "namespace Evil;\n" +
        "public class Hostile\n" +
        "{\n" +
        "    public void Trigger() { }\n" +
        "}\n";

    public const string ApiSourceText =
        "namespace ServeApi;\n" +
        "public class Catalog\n" +
        "{\n" +
        "    public void Lookup() { }\n" +
        "}\n" +
        "public class Helper\n" +
        "{\n" +
        "    public void Assist() { }\n" +
        "}\n";

    private const string RecipeId = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string RecipeIdB = "5555555555555555555555555555555555555555555555555555555555555555";
    private const string ToolInstanceId = "tool-instance-serve";
    private const string ProfileDigest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string PolicyDigest = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string GameSnapshotId = "snapshot-serve-game";
    private const string ApiSnapshotId = "snapshot-serve-api";
    private const int PagedTypeCount = 30;

    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-16T00:00:00Z");

    private SyntheticAtlas(string root)
    {
        DataRoot = root;
    }

    public string DataRoot { get; }

    public static async Task<SyntheticAtlas> SeedServeFixtureAsync(
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-serve-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var repository = new SqliteAtlasRepository(
            Path.Combine(root, "atlas.db"),
            Path.Combine(root, "backups"));
        await repository.InitializeAsync(cancellationToken);
        await ExtractionSeed.SeedToolInstanceAsync(root, ToolInstanceId, cancellationToken);
        var seed = await SeedBuildAsync(repository, root, BuildIdValue, RecipeId, BaseTime, cancellationToken);

        await SeedGameIndexAsync(
            repository,
            root,
            seed.Snapshot,
            GameIndexIdValue,
            GameSnapshotId,
            GameIndexVariant.Current,
            seed.ExtractionId,
            20,
            21,
            cancellationToken);
        await SeedApiIndexAsync(repository, root, cancellationToken);

        return new SyntheticAtlas(root);
    }

    public static async Task<SyntheticAtlas> SeedTwoBuildFixtureAsync(
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-serve-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var repository = new SqliteAtlasRepository(
            Path.Combine(root, "atlas.db"),
            Path.Combine(root, "backups"));
        await repository.InitializeAsync(cancellationToken);
        await ExtractionSeed.SeedToolInstanceAsync(root, ToolInstanceId, cancellationToken);

        var seedA = await SeedBuildAsync(repository, root, BuildIdAValue, RecipeId, BaseTime, cancellationToken);
        await SeedGameIndexAsync(
            repository,
            root,
            seedA.Snapshot,
            GameIndexAValue,
            "snapshot-serve-a",
            GameIndexVariant.Historical,
            seedA.ExtractionId,
            20,
            21,
            cancellationToken);

        var baseB = BaseTime.AddHours(1);
        var seedB = await SeedBuildAsync(repository, root, BuildIdBValue, RecipeIdB, baseB, cancellationToken);
        await SeedGameIndexAsync(
            repository,
            root,
            seedB.Snapshot,
            GameIndexBValue,
            "snapshot-serve-b",
            GameIndexVariant.Current,
            seedB.ExtractionId,
            80,
            81,
            cancellationToken);

        await SeedApiIndexAsync(repository, root, cancellationToken);

        return new SyntheticAtlas(root);
    }

    private sealed record SeededServeBuild(EnvironmentSnapshot Snapshot, string ExtractionId);

    private static async Task<SeededServeBuild> SeedBuildAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        string buildId,
        string recipeId,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        var snapshot = CreateServeSnapshot(buildId, baseTime);
        await repository.SaveSnapshotAsync(snapshot, cancellationToken);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository,
            dataRoot,
            buildId,
            recipeId,
            ToolInstanceId,
            ProfileDigest,
            PolicyDigest,
            baseTime,
            cancellationToken);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);
        return new SeededServeBuild(snapshot, seeded.Extraction.ExtractionId);
    }

    private static EnvironmentSnapshot CreateServeSnapshot(string buildId, DateTimeOffset baseTime) =>
        ExtractionSeed.CreateSnapshot(buildId, baseTime, LeakInstallationRoot, ServeDependencies());

    private static IReadOnlyList<DependencyVersion> ServeDependencies() =>
    [
        new DependencyVersion(
            DependencyKind.MelonLoader,
            "1.0.0",
            "\\\\servefake-host\\share\\mods\\FakeMod.dll",
            true,
            new string('e', 64)),
        new DependencyVersion(
            DependencyKind.Sideload,
            "0.2",
            $"D:\\{LeakOutsideToken}\\tools\\HelperMod.dll",
            true,
            new string('f', 64)),
        new DependencyVersion(
            DependencyKind.Sideload,
            "0.3",
            "mods\\LocalMod.dll",
            true,
            null),
        new DependencyVersion(
            DependencyKind.S1Api,
            "9.9",
            null,
            false,
            null),
    ];

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(DataRoot);
    }

    private static string PagedSourceText()
    {
        var builder = new StringBuilder("namespace Paged;\n");
        for (var number = 1; number <= PagedTypeCount; number++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"public class PagedType{number:00}\n{{\n}}\n");
        }

        return builder.ToString();
    }

    // The current variant is the full Widget graph (thirty paged types and the
    // hostile type included); the historical variant drops the paged and
    // hostile types plus CheckPhysics and adds Turbo, so A-to-B diffs cover
    // every classification. Symbol IDs take a per-index prefix because they
    // are globally unique; canonical keys stay identical across variants.
    private sealed record GameIndexVariant(
        string IdPrefix,
        bool IncludeCheckPhysics,
        bool IncludeHostile,
        bool IncludePaged,
        bool IncludeTurbo,
        string RunBodyFingerprint)
    {
        public static GameIndexVariant Current { get; } =
            new("", true, true, true, false, "fp-run-body-current");

        public static GameIndexVariant Historical { get; } =
            new("a-", false, false, false, true, "fp-run-body-historical");
    }

    private static async Task SeedGameIndexAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        EnvironmentSnapshot snapshot,
        string gameIndexId,
        string snapshotId,
        GameIndexVariant variant,
        string extractionId,
        int createdMinutes,
        int completedMinutes,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = BaseTime.AddMinutes(createdMinutes).ToString("O");
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                extractionId,
                createdAtUtc,
                EnvironmentSnapshotId.Create(snapshot)),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(
                gameIndexId,
                snapshotId,
                IndexRunStatus.Running,
                createdAtUtc),
            cancellationToken);

        var indexRoot = Path.Combine(
            dataRoot, "builds", snapshot.Build.BuildId, "indexes", gameIndexId);
        var files = new List<IndexSourceFileRecord>
        {
            await WriteSourceFileAsync(
                indexRoot, snapshotId, "Assembly-CSharp.cs", GameSourceText, cancellationToken)
        };
        IndexSourceFileRecord? hostileFile = null;
        if (variant.IncludePaged)
        {
            files.Add(await WriteSourceFileAsync(
                indexRoot, snapshotId, "Paged.cs", PagedSourceText(), cancellationToken));
        }

        if (variant.IncludeHostile)
        {
            hostileFile = await WriteSourceFileAsync(
                indexRoot, snapshotId, "Hostile.cs", HostileSourceText, cancellationToken);
            files.Add(hostileFile);
        }

        string Sid(string id) => variant.IdPrefix + id;
        var symbols = new List<IndexSymbolRecord>
        {
            new(
                Sid(WidgetTypeId),
                snapshotId,
                "ScheduleI:Installed:Type:Demo.Widget",
                "Type",
                TypeSelector,
                TypeSelector,
                false),
            new(
                Sid(RunMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::Run()",
                "Method",
                "Demo.Widget.Run",
                RunSelector,
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(StateFieldId),
                snapshotId,
                "ScheduleI:Installed:Field:Demo.Widget::System.Int32 _state",
                "Field",
                "Demo.Widget._state",
                "System.Int32 Demo.Widget::_state",
                false),
            new(
                Sid(CallerMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Caller::Invoke()",
                "Method",
                "Demo.Caller.Invoke",
                "System.Void Demo.Caller::Invoke()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(ExecuteMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Service::Execute()",
                "Method",
                "Demo.Service.Execute",
                "System.Void Demo.Service::Execute()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(BaseTypeId),
                snapshotId,
                "ScheduleI:Installed:Type:Demo.WidgetBase",
                "Type",
                "Demo.WidgetBase",
                "Demo.WidgetBase",
                false),
            new(
                Sid(BaseRenderMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.WidgetBase::Render()",
                "Method",
                "Demo.WidgetBase.Render",
                "System.Void Demo.WidgetBase::Render()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(RenderMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::Render()",
                "Method",
                "Demo.Widget.Render",
                "System.Void Demo.Widget::Render()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(PayloadTypeId),
                snapshotId,
                "ScheduleI:Installed:Type:Demo.Payload",
                "Type",
                "Demo.Payload",
                "Demo.Payload",
                false),
            new(
                Sid(ResultTypeId),
                snapshotId,
                "ScheduleI:Installed:Type:Demo.Result",
                "Type",
                "Demo.Result",
                "Demo.Result",
                false),
            new(
                Sid(CreditFooMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Credit::Foo()",
                "Method",
                "Demo.Credit.Foo",
                "System.Void Demo.Credit::Foo()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(CreditLambdaMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Credit::Foo+<>c::<Foo>b__0_0()",
                "Method",
                "Demo.Credit.Foo+<>c::<Foo>b__0_0",
                "System.Void Demo.Credit::Foo+<>c::<Foo>b__0_0()",
                false,
                BodyRecoveryStatus.Recovered,
                IsGenerated: true),
            new(
                Sid(CreditLeafMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Credit::Leaf()",
                "Method",
                "Demo.Credit.Leaf",
                "System.Void Demo.Credit::Leaf()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                Sid(CreditCaptureFieldId),
                snapshotId,
                "ScheduleI:Installed:Field:Demo.Capture::System.Int32 Holder+<>c__DisplayClass0_0::x",
                "Field",
                "Demo.Capture.Holder+<>c__DisplayClass0_0::x",
                "System.Int32 Demo.Capture::Holder+<>c__DisplayClass0_0::x",
                false,
                IsGenerated: true),
        };
        if (variant.IncludeCheckPhysics)
        {
            symbols.Add(new IndexSymbolRecord(
                Sid(CheckPhysicsMethodId),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::CheckPhysics()",
                "Method",
                "Demo.Widget.CheckPhysics",
                "System.Void Demo.Widget::CheckPhysics()",
                false,
                BodyRecoveryStatus.Recovered));
        }

        if (variant.IncludeHostile)
        {
            symbols.Add(new IndexSymbolRecord(
                Sid(HostileTypeId),
                snapshotId,
                "ScheduleI:Installed:Type:Evil.Hostile",
                "Type",
                HostileQualifiedName,
                "Evil.Hostile",
                false));
        }

        if (variant.IncludeTurbo)
        {
            symbols.Add(new IndexSymbolRecord(
                Sid("method-serve-turbo"),
                snapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::Turbo()",
                "Method",
                TurboQualifiedName,
                "System.Void Demo.Widget::Turbo()",
                false,
                BodyRecoveryStatus.Recovered));
        }

        if (variant.IncludePaged)
        {
            for (var number = 1; number <= PagedTypeCount; number++)
            {
                var name = string.Format(
                    CultureInfo.InvariantCulture, "Paged.PagedType{0:00}", number);
                symbols.Add(new IndexSymbolRecord(
                    Sid(string.Format(CultureInfo.InvariantCulture, "type-serve-paged-{0:00}", number)),
                    snapshotId,
                    "ScheduleI:Installed:Type:" + name,
                    "Type",
                    name,
                    name,
                    false));
            }
        }

        var widgetFileId = files[0].SourceFileId;
        var locations = new List<IndexSourceLocationRecord>
        {
            new(Sid(WidgetTypeId), widgetFileId, 5, 1, 10, 2),
            new(Sid(RunMethodId), widgetFileId, 8, 5, 8, 26),
            new(Sid(StateFieldId), widgetFileId, 7, 5, 7, 22),
        };
        if (variant.IncludeCheckPhysics)
        {
            locations.Add(new IndexSourceLocationRecord(
                Sid(CheckPhysicsMethodId), widgetFileId, 9, 5, 9, 71));
        }

        if (variant.IncludeHostile && hostileFile is not null)
        {
            locations.Add(new IndexSourceLocationRecord(
                Sid(HostileTypeId), hostileFile.SourceFileId, 2, 1, 5, 2));
        }

        await repository.CompleteIndexRunAsync(
            gameIndexId,
            new IndexWriteSet(
                symbols,
                files,
                locations,
                [new IndexFingerprintRecord(Sid(RunMethodId), "method-body", variant.RunBodyFingerprint)],
                [
                    new IndexRelationshipRecord(
                        Sid("rel-serve-incoming"),
                        snapshotId,
                        Sid(CallerMethodId),
                        Sid(RunMethodId),
                        null,
                        "Calls",
                        "fixture:incoming-call"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-outgoing"),
                        snapshotId,
                        Sid(RunMethodId),
                        Sid(ExecuteMethodId),
                        null,
                        "Calls",
                        "fixture:outgoing-call"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-inherits"),
                        snapshotId,
                        Sid(WidgetTypeId),
                        Sid(BaseTypeId),
                        null,
                        "Inherits",
                        "fixture:inherits"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-overrides"),
                        snapshotId,
                        Sid(RenderMethodId),
                        Sid(BaseRenderMethodId),
                        null,
                        "Overrides",
                        "fixture:overrides"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-dispatch-virtual"),
                        snapshotId,
                        Sid(CallerMethodId),
                        Sid(BaseRenderMethodId),
                        null,
                        "CallsVirtual",
                        "fixture:dispatch-virtual"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-dispatch-direct"),
                        snapshotId,
                        Sid(ExecuteMethodId),
                        Sid(RenderMethodId),
                        null,
                        "Calls",
                        "fixture:dispatch-direct"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-parameter"),
                        snapshotId,
                        Sid(RunMethodId),
                        Sid(PayloadTypeId),
                        null,
                        "ParameterType",
                        "fixture:parameter-type"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-return"),
                        snapshotId,
                        Sid(RunMethodId),
                        Sid(ResultTypeId),
                        null,
                        "ReturnType",
                        "fixture:return-type"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-reads"),
                        snapshotId,
                        Sid(RunMethodId),
                        Sid(StateFieldId),
                        null,
                        "ReadsField",
                        "fixture:reads-field"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-writes"),
                        snapshotId,
                        Sid(ExecuteMethodId),
                        Sid(StateFieldId),
                        null,
                        "WritesField",
                        "fixture:writes-field"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-credit-call"),
                        snapshotId,
                        Sid(CreditFooMethodId),
                        Sid(CreditLeafMethodId),
                        null,
                        "Calls",
                        "fixture:credit-call",
                        GeneratedSourceSymbolId: Sid(CreditLambdaMethodId),
                        GeneratedDetail: "in lambda"),
                    new IndexRelationshipRecord(
                        Sid("rel-serve-credit-reads"),
                        snapshotId,
                        Sid(CreditFooMethodId),
                        Sid(CreditCaptureFieldId),
                        null,
                        "ReadsField",
                        "fixture:credit-reads"),
                ]),
            BaseTime.AddMinutes(completedMinutes).ToString("O"),
            cancellationToken);
    }

    private static async Task SeedApiIndexAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = BaseTime.AddMinutes(30).ToString("O");
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                ApiSnapshotId,
                CodebaseKind.S1Api,
                CodeChannel.Release,
                ApiCommitShaValue,
                createdAtUtc),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(
                ApiIndexIdValue,
                ApiSnapshotId,
                IndexRunStatus.Running,
                createdAtUtc),
            cancellationToken);

        var indexRoot = Path.Combine(
            dataRoot,
            "upstream", "s1api", "commits", ApiCommitShaValue, "indexes", ApiIndexIdValue);
        var apiFile = await WriteSourceFileAsync(
            indexRoot, ApiSnapshotId, "S1Api.cs", ApiSourceText, cancellationToken);

        await repository.CompleteIndexRunAsync(
            ApiIndexIdValue,
            new IndexWriteSet(
                [
                    new IndexSymbolRecord(
                        CatalogTypeId,
                        ApiSnapshotId,
                        "S1Api:Release:Type:ServeApi.Catalog",
                        "Type",
                        "ServeApi.Catalog",
                        "ServeApi.Catalog",
                        false),
                    new IndexSymbolRecord(
                        LookupMethodId,
                        ApiSnapshotId,
                        "S1Api:Release:Method:ServeApi.Catalog::Lookup()",
                        "Method",
                        "ServeApi.Catalog.Lookup",
                        LookupSelector,
                        false,
                        BodyRecoveryStatus.Recovered),
                    new IndexSymbolRecord(
                        AssistMethodId,
                        ApiSnapshotId,
                        "S1Api:Release:Method:ServeApi.Helper::Assist()",
                        "Method",
                        "ServeApi.Helper.Assist",
                        "System.Void ServeApi.Helper::Assist()",
                        false,
                        BodyRecoveryStatus.Recovered),
                ],
                [apiFile],
                [
                    new IndexSourceLocationRecord(
                        CatalogTypeId, apiFile.SourceFileId, 2, 1, 5, 2),
                    new IndexSourceLocationRecord(
                        LookupMethodId, apiFile.SourceFileId, 4, 5, 4, 26),
                ],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-serve-api-call",
                        ApiSnapshotId,
                        AssistMethodId,
                        LookupMethodId,
                        null,
                        "Calls",
                        "fixture:api-call"),
                ]),
            BaseTime.AddMinutes(31).ToString("O"),
            cancellationToken);
    }

    private static async Task<IndexSourceFileRecord> WriteSourceFileAsync(
        string indexRoot,
        string snapshotId,
        string relativePath,
        string sourceText,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(indexRoot);
        var sourceFile = new IndexSourceFileRecord(
            "source-" + snapshotId + "-" + relativePath.Replace(".", "-", StringComparison.Ordinal).ToLowerInvariant(),
            snapshotId,
            relativePath,
            ExtractionSeed.Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, relativePath),
            sourceText,
            new UTF8Encoding(false),
            cancellationToken);
        return sourceFile;
    }
}
