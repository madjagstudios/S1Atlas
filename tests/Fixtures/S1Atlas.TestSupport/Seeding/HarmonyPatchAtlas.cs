using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Source;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.TestSupport.Seeding;

public sealed record HarmonyPatchSeed(
    string BuildId,
    string ExtractionId,
    string GameIndexId,
    string GameSnapshotId,
    string Collection,
    string ModId,
    string ReferenceIndexId,
    string ReferenceSnapshotId);

/// <summary>
/// Seeds a build, a game index, and a reference collection from the compiled
/// harmony fixture assemblies through the real decompiler, the real game symbol
/// builder, and the real reference-collection workflow. Symbol records always
/// have the real indexer shape; nothing is hand-written.
/// </summary>
public static class HarmonyPatchAtlas
{
    public const string CollectionId = "harmony";
    public const string ModId = "harmony-fixture";

    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
    private const string RecipeId = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static string GameFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyGameFixture.dll");

    public static string ModFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll");

    public static async Task<HarmonyPatchSeed> SeedAsync(
        SqliteAtlasRepository repository,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var buildId = IndexingWorkflow.HashId("harmony-build");
        await ExtractionSeed.SeedToolInstanceAsync(dataRoot, ToolInstanceId, cancellationToken);
        await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(buildId, BaseTime), cancellationToken);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository,
            dataRoot,
            buildId,
            RecipeId,
            ToolInstanceId,
            ProfileDigest,
            PolicyDigest,
            BaseTime,
            cancellationToken);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);

        var gameIndexId = IndexingWorkflow.HashId("harmony-game-index");
        var gameSnapshotId = "schedule-i:" + seeded.Extraction.ExtractionId + ":" + gameIndexId;
        var createdAtUtc = BaseTime.AddMinutes(20).ToString("O");
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                gameSnapshotId,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                seeded.Extraction.ExtractionId,
                createdAtUtc,
                EnvironmentSnapshotId.Create(ExtractionSeed.CreateSnapshot(buildId, BaseTime))),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(gameIndexId, gameSnapshotId, IndexRunStatus.Running, createdAtUtc),
            cancellationToken);

        var decompilation = await new IlSpyManagedDecompiler().DecompileAsync(GameFixturePath, cancellationToken);
        var symbols = IndexingWorkflow.BuildSymbols(decompilation, gameSnapshotId);
        var paths = OwnedIndexPaths.ForScheduleOne(dataRoot, buildId, gameIndexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var sourceFile = await new GeneratedSourceWriter().WriteAsync(
            paths.StagingRoot,
            "Assembly-CSharp.cs",
            decompilation.SourceText,
            gameSnapshotId,
            cancellationToken);
        var sourceSymbols = new RoslynSourceIndexer().Index(
            decompilation.SourceText,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            sourceFile.RelativePath);
        var locations = IndexingWorkflow.BuildSourceLocations(sourceSymbols, symbols, sourceFile);
        await repository.CompleteIndexRunAsync(
            gameIndexId,
            new IndexWriteSet(symbols, [sourceFile], locations, [], []),
            BaseTime.AddMinutes(21).ToString("O"),
            cancellationToken);
        if (Directory.Exists(paths.FinalRoot))
            Directory.Delete(paths.FinalRoot, recursive: true);
        Directory.Move(paths.StagingRoot, paths.FinalRoot);
        await File.WriteAllTextAsync(paths.CompleteMarkerPath!, gameIndexId + "\n", Encoding.UTF8, cancellationToken);

        var modRoot = Path.Combine(dataRoot, "reference-input", CollectionId);
        Directory.CreateDirectory(Path.Combine(modRoot, "plugins"));
        File.Copy(ModFixturePath, Path.Combine(modRoot, "plugins", "HarmonyMod.dll"), overwrite: true);
        var definition = new ReferenceCollectionDefinition(
            buildId,
            gameIndexId,
            [new ReferenceModDefinition(ModId, "Harmony Fixture", "1.0.0", null, modRoot, "declared-content", ["plugins/**"])],
            CollectionId,
            "Harmony Fixture");
        var workflow = new ReferenceModIndexWorkflow(
            dataRoot,
            repository,
            new ReferenceModFileSelector(),
            new ReferenceModInputHasher(),
            new ReferenceModIndexSource(new IlSpyManagedDecompiler()),
            new ReferenceGameSymbolLoader(repository));
        var result = await workflow.RunAsync(buildId, definition, false, cancellationToken);

        return new HarmonyPatchSeed(
            buildId,
            seeded.Extraction.ExtractionId,
            gameIndexId,
            gameSnapshotId,
            CollectionId,
            ModId,
            result.IndexId,
            "reference:" + gameIndexId + ":" + result.IndexId);
    }
}
