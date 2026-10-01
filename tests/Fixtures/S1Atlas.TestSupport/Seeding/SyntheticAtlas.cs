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
    public const string PayloadTypeId = "type-serve-payload";
    public const string ResultTypeId = "type-serve-result";
    public const string HostileTypeId = "type-serve-hostile";
    public const string HostileQualifiedName = "Evil.<img src=x onerror=alert(1)>";

    public const string CatalogTypeId = "type-serve-catalog";
    public const string LookupMethodId = "method-serve-lookup";
    public const string AssistMethodId = "method-serve-assist";

    public const string TypeSelector = "Demo.Widget";
    public const string RunSelector = "System.Void Demo.Widget::Run()";
    public const string LookupSelector = "System.Void ServeApi.Catalog::Lookup()";

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
        await repository.SaveSnapshotAsync(
            ExtractionSeed.CreateSnapshot(BuildIdValue, BaseTime),
            cancellationToken);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository,
            root,
            BuildIdValue,
            RecipeId,
            ToolInstanceId,
            ProfileDigest,
            PolicyDigest,
            BaseTime,
            cancellationToken);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                BuildIdValue,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);

        await SeedGameIndexAsync(repository, root, seeded.Extraction.ExtractionId, cancellationToken);
        await SeedApiIndexAsync(repository, root, cancellationToken);

        return new SyntheticAtlas(root);
    }

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

    private static async Task SeedGameIndexAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        string extractionId,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = BaseTime.AddMinutes(20).ToString("O");
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                GameSnapshotId,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                extractionId,
                createdAtUtc,
                EnvironmentSnapshotId.Create(
                    ExtractionSeed.CreateSnapshot(BuildIdValue, BaseTime))),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(
                GameIndexIdValue,
                GameSnapshotId,
                IndexRunStatus.Running,
                createdAtUtc),
            cancellationToken);

        var indexRoot = Path.Combine(
            dataRoot, "builds", BuildIdValue, "indexes", GameIndexIdValue);
        var widgetFile = await WriteSourceFileAsync(
            indexRoot, GameSnapshotId, "Assembly-CSharp.cs", GameSourceText, cancellationToken);
        var pagedFile = await WriteSourceFileAsync(
            indexRoot, GameSnapshotId, "Paged.cs", PagedSourceText(), cancellationToken);
        var hostileFile = await WriteSourceFileAsync(
            indexRoot, GameSnapshotId, "Hostile.cs", HostileSourceText, cancellationToken);

        var symbols = new List<IndexSymbolRecord>
        {
            new(
                WidgetTypeId,
                GameSnapshotId,
                "ScheduleI:Installed:Type:Demo.Widget",
                "Type",
                TypeSelector,
                TypeSelector,
                false),
            new(
                RunMethodId,
                GameSnapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::Run()",
                "Method",
                "Demo.Widget.Run",
                RunSelector,
                false,
                BodyRecoveryStatus.Recovered),
            new(
                CheckPhysicsMethodId,
                GameSnapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::CheckPhysics()",
                "Method",
                "Demo.Widget.CheckPhysics",
                "System.Void Demo.Widget::CheckPhysics()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                StateFieldId,
                GameSnapshotId,
                "ScheduleI:Installed:Field:Demo.Widget::System.Int32 _state",
                "Field",
                "Demo.Widget._state",
                "System.Int32 Demo.Widget::_state",
                false),
            new(
                CallerMethodId,
                GameSnapshotId,
                "ScheduleI:Installed:Method:Demo.Caller::Invoke()",
                "Method",
                "Demo.Caller.Invoke",
                "System.Void Demo.Caller::Invoke()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                ExecuteMethodId,
                GameSnapshotId,
                "ScheduleI:Installed:Method:Demo.Service::Execute()",
                "Method",
                "Demo.Service.Execute",
                "System.Void Demo.Service::Execute()",
                false,
                BodyRecoveryStatus.Recovered),
            new(
                BaseTypeId,
                GameSnapshotId,
                "ScheduleI:Installed:Type:Demo.WidgetBase",
                "Type",
                "Demo.WidgetBase",
                "Demo.WidgetBase",
                false),
            new(
                PayloadTypeId,
                GameSnapshotId,
                "ScheduleI:Installed:Type:Demo.Payload",
                "Type",
                "Demo.Payload",
                "Demo.Payload",
                false),
            new(
                ResultTypeId,
                GameSnapshotId,
                "ScheduleI:Installed:Type:Demo.Result",
                "Type",
                "Demo.Result",
                "Demo.Result",
                false),
            new(
                HostileTypeId,
                GameSnapshotId,
                "ScheduleI:Installed:Type:Evil.Hostile",
                "Type",
                HostileQualifiedName,
                "Evil.Hostile",
                false),
        };
        for (var number = 1; number <= PagedTypeCount; number++)
        {
            var name = string.Format(
                CultureInfo.InvariantCulture, "Paged.PagedType{0:00}", number);
            symbols.Add(new IndexSymbolRecord(
                string.Format(CultureInfo.InvariantCulture, "type-serve-paged-{0:00}", number),
                GameSnapshotId,
                "ScheduleI:Installed:Type:" + name,
                "Type",
                name,
                name,
                false));
        }

        await repository.CompleteIndexRunAsync(
            GameIndexIdValue,
            new IndexWriteSet(
                symbols,
                [widgetFile, pagedFile, hostileFile],
                [
                    new IndexSourceLocationRecord(
                        WidgetTypeId, widgetFile.SourceFileId, 5, 1, 10, 2),
                    new IndexSourceLocationRecord(
                        RunMethodId, widgetFile.SourceFileId, 8, 5, 8, 26),
                    new IndexSourceLocationRecord(
                        CheckPhysicsMethodId, widgetFile.SourceFileId, 9, 5, 9, 71),
                    new IndexSourceLocationRecord(
                        StateFieldId, widgetFile.SourceFileId, 7, 5, 7, 22),
                    new IndexSourceLocationRecord(
                        HostileTypeId, hostileFile.SourceFileId, 2, 1, 5, 2),
                ],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-serve-incoming",
                        GameSnapshotId,
                        CallerMethodId,
                        RunMethodId,
                        null,
                        "Calls",
                        "fixture:incoming-call"),
                    new IndexRelationshipRecord(
                        "rel-serve-outgoing",
                        GameSnapshotId,
                        RunMethodId,
                        ExecuteMethodId,
                        null,
                        "Calls",
                        "fixture:outgoing-call"),
                    new IndexRelationshipRecord(
                        "rel-serve-inherits",
                        GameSnapshotId,
                        WidgetTypeId,
                        BaseTypeId,
                        null,
                        "Inherits",
                        "fixture:inherits"),
                    new IndexRelationshipRecord(
                        "rel-serve-parameter",
                        GameSnapshotId,
                        RunMethodId,
                        PayloadTypeId,
                        null,
                        "ParameterType",
                        "fixture:parameter-type"),
                    new IndexRelationshipRecord(
                        "rel-serve-return",
                        GameSnapshotId,
                        RunMethodId,
                        ResultTypeId,
                        null,
                        "ReturnType",
                        "fixture:return-type"),
                    new IndexRelationshipRecord(
                        "rel-serve-reads",
                        GameSnapshotId,
                        RunMethodId,
                        StateFieldId,
                        null,
                        "ReadsField",
                        "fixture:reads-field"),
                    new IndexRelationshipRecord(
                        "rel-serve-writes",
                        GameSnapshotId,
                        ExecuteMethodId,
                        StateFieldId,
                        null,
                        "WritesField",
                        "fixture:writes-field"),
                ]),
            BaseTime.AddMinutes(21).ToString("O"),
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
            "source-serve-" + relativePath.Replace(".", "-", StringComparison.Ordinal).ToLowerInvariant(),
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
