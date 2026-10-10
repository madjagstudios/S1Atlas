using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Fingerprints;
using S1Atlas.Indexing.Paths;
using S1Atlas.Indexing.Source;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.TestSupport.Seeding;

public sealed record ModCheckSeed(string FromBuildId, string ToBuildId, string FromIndexId, string ToIndexId, string ModPath);

public enum ModCheckRegressionVariant
{
    None,
    DynamicPatch,
    MissingSignatureType,
    TypeTokenOnly,
    OtherModReference,
    ExternalHarmony,
    InteropReflection
}

/// <summary>Compiles scratch assemblies, then indexes their actual metadata and IL.</summary>
public static class ModCheckAtlas
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
    private const string ToolInstanceId = "mod-check-tool";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static async Task<ModCheckSeed> SeedAsync(SqliteAtlasRepository repository, string dataRoot, CancellationToken ct, bool includeFrom = true, bool includeTo = true, ModCheckRegressionVariant regressionVariant = ModCheckRegressionVariant.None)
    {
        var scratch = Path.Combine(dataRoot, "mod-check-inputs");
        Directory.CreateDirectory(scratch);
        var fromAssembly = Path.Combine(scratch, "game-a", "ModCheckGame.dll");
        var toAssembly = Path.Combine(scratch, "game-b", "ModCheckGame.dll");
        Compile(GameA, fromAssembly);
        Compile(GameB, toAssembly);
        var interopStub = Path.Combine(scratch, "compile-references", "InteropStub.dll");
        Compile(InteropSource, interopStub);
        var otherModStub = Path.Combine(scratch, "compile-references", "OtherModStub.dll");
        if (regressionVariant is ModCheckRegressionVariant.OtherModReference or ModCheckRegressionVariant.ExternalHarmony)
            Compile(OtherModSource, otherModStub);
        var modPath = Path.Combine(scratch, "plugins", "ModCheckPlugin.dll");
        var variantSource = regressionVariant switch
        {
            ModCheckRegressionVariant.DynamicPatch => DynamicPatchSource,
            ModCheckRegressionVariant.MissingSignatureType => MissingSignatureTypeSource,
            ModCheckRegressionVariant.TypeTokenOnly => TypeTokenOnlySource,
            ModCheckRegressionVariant.OtherModReference => OtherModReferenceSource,
            ModCheckRegressionVariant.ExternalHarmony => ExternalHarmonySource,
            ModCheckRegressionVariant.InteropReflection => InteropReflectionSource,
            _ => string.Empty
        };
        var references = regressionVariant is ModCheckRegressionVariant.OtherModReference or ModCheckRegressionVariant.ExternalHarmony
            ? new[] { fromAssembly, interopStub, otherModStub }
            : new[] { fromAssembly, interopStub };
        Compile(ModSource + "\n" + variantSource, modPath, references);

        var fromBuild = IndexingWorkflow.HashId("mod-check-build-a");
        var toBuild = IndexingWorkflow.HashId("mod-check-build-b");
        var fromIndex = IndexingWorkflow.HashId("mod-check-index-a");
        var toIndex = IndexingWorkflow.HashId("mod-check-index-b");
        await ExtractionSeed.SeedToolInstanceAsync(dataRoot, ToolInstanceId, ct);
        if (includeFrom)
            await SeedBuildAsync(repository, dataRoot, fromBuild, fromIndex, fromAssembly, BaseTime, ct);
        await SeedBuildAsync(repository, dataRoot, toBuild, toIndex, toAssembly, BaseTime.AddHours(1), ct, createIndex: includeTo);
        return new ModCheckSeed(fromBuild, toBuild, fromIndex, toIndex, modPath);
    }

    public static async Task<ModCheckSeed> SeedInteropProjectionAsync(
        SqliteAtlasRepository repository, string dataRoot, bool includeCallableSurface, CancellationToken ct,
        bool includeTypeProjections = false, bool publicField = false, bool includePropertyAccessors = false,
        bool includeReflectionAccessors = false, bool getterDecoy = false)
    {
        var scratch = Path.Combine(dataRoot, "interop-projection-inputs");
        var fromAssembly = Path.Combine(scratch, "game-a", "ModCheckGame.dll");
        var toAssembly = Path.Combine(scratch, "game-b", "ModCheckGame.dll");
        var interopAssembly = Path.Combine(scratch, "interop", "InteropProjection.dll");
        var modPath = Path.Combine(scratch, "plugins", "InteropProjectionPlugin.dll");
        var gameA = publicField ? ProjectionGameA.Replace("private int Count;", "public int Count;", StringComparison.Ordinal) : ProjectionGameA;
        Compile(gameA + (includeTypeProjections ? ProjectionArrayGameA : "")
            + (getterDecoy ? ProjectionGetterDecoyGame : ""), fromAssembly);
        Compile(ProjectionGameB + (includeTypeProjections ? ProjectionArrayGameB : "")
            + (getterDecoy ? ProjectionGetterDecoyGame : ""), toAssembly);
        Compile(ProjectionInterop + (includeTypeProjections ? ProjectionArrayInterop : "")
            + (includePropertyAccessors ? ProjectionPropertyInterop : "")
            + (includeReflectionAccessors ? ProjectionReflectionInterop : "")
            + (getterDecoy ? ProjectionGetterDecoyInterop : ""), interopAssembly);
        Compile((getterDecoy ? "" : ProjectionMod) + (includeTypeProjections ? ProjectionArrayMod : "")
            + (includePropertyAccessors ? ProjectionPropertyMod : "")
            + (includeReflectionAccessors ? ProjectionReflectionMod : ""), modPath, interopAssembly);

        var fromBuild = IndexingWorkflow.HashId("interop-projection-build-a");
        var toBuild = IndexingWorkflow.HashId("interop-projection-build-b");
        var fromIndex = IndexingWorkflow.HashId("interop-projection-index-a");
        var toIndex = IndexingWorkflow.HashId("interop-projection-index-b");
        await ExtractionSeed.SeedToolInstanceAsync(dataRoot, ToolInstanceId, ct);
        await SeedBuildAsync(repository, dataRoot, fromBuild, fromIndex, fromAssembly, BaseTime, ct,
            interopPath: interopAssembly, includeCallableSurface: includeCallableSurface);
        await SeedBuildAsync(repository, dataRoot, toBuild, toIndex, toAssembly, BaseTime.AddHours(1), ct,
            interopPath: interopAssembly, includeCallableSurface: includeCallableSurface);
        return new ModCheckSeed(fromBuild, toBuild, fromIndex, toIndex, modPath);
    }

    public static async Task<string> SeedAmbiguousFromAsync(SqliteAtlasRepository repository, string dataRoot, CancellationToken ct)
    {
        var original = IndexingWorkflow.HashId("mod-check-build-a");
        var ambiguous = original[..12] + new string('f', 52);
        await SeedBuildAsync(repository, dataRoot, ambiguous, IndexingWorkflow.HashId("mod-check-index-a-ambiguous"),
            Path.Combine(dataRoot, "mod-check-inputs", "game-a", "ModCheckGame.dll"), BaseTime.AddMinutes(30), ct);
        return ambiguous;
    }

    private static async Task SeedBuildAsync(SqliteAtlasRepository repository, string dataRoot, string buildId, string indexId, string assemblyPath, DateTimeOffset at, CancellationToken ct,
        bool createIndex = true, string? interopPath = null, bool includeCallableSurface = false)
    {
        var environment = ExtractionSeed.CreateSnapshot(buildId, at);
        await repository.SaveSnapshotAsync(environment, ct);
        var recipeId = IndexingWorkflow.HashId("mod-check-recipe-" + buildId);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(repository, dataRoot, buildId, recipeId, ToolInstanceId, ProfileDigest, PolicyDigest, at, ct);
        await repository.SetPreferredExtractionAsync(new PreferredExtraction(buildId, seeded.Extraction.ExtractionId, seeded.Report.ValidatedAtUtc, ExtractionPreferenceReason.ManualPromotion), ct);
        if (!createIndex)
            return;
        var snapshotId = "schedule-i:" + seeded.Extraction.ExtractionId + ":" + indexId;
        await repository.CreateCodeSnapshotAsync(new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, seeded.Extraction.ExtractionId, at.AddMinutes(20).ToString("O"), EnvironmentSnapshotId.Create(environment)), ct);
        await repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, at.AddMinutes(20).ToString("O")), ct);

        var decompilation = await new IlSpyManagedDecompiler().DecompileAsync(assemblyPath, ct);
        var symbols = IndexingWorkflow.BuildSymbols(decompilation, snapshotId);
        var paths = OwnedIndexPaths.ForScheduleOne(dataRoot, buildId, indexId);
        Directory.CreateDirectory(paths.StagingRoot);
        var sourceFile = await new GeneratedSourceWriter().WriteAsync(paths.StagingRoot, "Assembly-CSharp.cs", decompilation.SourceText, snapshotId, ct);
        var sourceFiles = new List<IndexSourceFileRecord> { sourceFile };
        IReadOnlyList<IndexCallableSurfaceRecord> callableSurface = [];
        if (includeCallableSurface)
        {
            var interop = await new IlSpyManagedDecompiler().DecompileAsync(interopPath!, ct);
            await using var interopStream = File.OpenRead(interopPath!);
            var interopHash = Convert.ToHexString(await SHA256.HashDataAsync(interopStream, ct)).ToLowerInvariant();
            sourceFiles.Add(await new GeneratedSourceWriter().WriteAsync(paths.StagingRoot,
                "interop/Assembly-CSharp.cs", interop.SourceText, snapshotId, ct));
            callableSurface = IndexingWorkflow.BuildCallableSurface(decompilation, interop, symbols,
                indexId, snapshotId, interopPath, interopHash);
        }
        var sourceSymbols = new RoslynSourceIndexer().Index(decompilation.SourceText, CodebaseKind.ScheduleI, CodeChannel.Installed, sourceFile.RelativePath);
        var locations = IndexingWorkflow.BuildSourceLocations(sourceSymbols, symbols, sourceFile);
        var evidence = decompilation.Types.SelectMany(type => type.Members.Select(member => (type, member)))
            .Where(item => item.member.HasBody && item.member.References.Count > 0)
            .Select(item => (Symbol: symbols.FirstOrDefault(symbol => symbol.QualifiedName == ManagedMemberIdentity.Render(item.type.FullName, item.member)),
                References: (IReadOnlyList<string>)item.member.References.Select(reference => reference.Kind + ":" + reference.Target).ToArray()))
            .Where(item => item.Symbol is not null)
            .ToDictionary(item => item.Symbol!.SymbolId, item => item.References, StringComparer.Ordinal);
        var fingerprints = new SymbolFingerprintService().Create(symbols, evidence);
        await repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, sourceFiles, locations, fingerprints, [], callableSurface), at.AddMinutes(21).ToString("O"), ct);
        Directory.Move(paths.StagingRoot, paths.FinalRoot);
        await File.WriteAllTextAsync(paths.CompleteMarkerPath!, indexId + "\n", Encoding.UTF8, ct);
    }

    private static void Compile(string source, string outputPath, params string[] extraReferences) =>
        ScratchAssembly.Compile(source, outputPath, extraReferences);

    private const string GameA = """
        namespace Demo {
          public sealed class Arg { }
          public static class Api {
            public static int Removed() => 1;
            public static int Single(int value) => value;
            public static void RenamedBefore() => System.Console.Error.WriteLine("rename-only");
            public static void Relocated() => System.GC.KeepAlive("move-only");
            public static void BodyOnly() => System.Console.WriteLine("body-a");
            public static void Stable() => System.Console.WriteLine("stable");
            public static int Count;
            public static void ReflectChanged(Arg value) { }
          }
          public static class Destination { }
        }
        """;

    private const string GameB = """
        namespace Demo {
          public sealed class Arg { }
          public static class Api {
            public static int Single(int value, string text) => value + text.Length;
            public static void RenamedAfter() => System.Console.Error.WriteLine("rename-only");
            public static void BodyOnly() => System.Console.Write("body-b");
            public static void Stable() => System.Console.WriteLine("stable");
            public static int Count;
            public static void ReflectChanged(Arg value, string extra) { }
          }
          public static class Destination {
            public static void Relocated() => System.GC.KeepAlive("move-only");
          }
        }
        """;

    private const string ModSource = """
        using System;
        using System.Reflection;
        using Demo;
        namespace HarmonyLib {
          [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
          public sealed class HarmonyPatch : Attribute { public HarmonyPatch() {} public HarmonyPatch(Type type, string name) {} }
          [AttributeUsage(AttributeTargets.Method)]
          public sealed class HarmonyPrefix : Attribute {}
          public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) {} }
          public sealed class Harmony { public void Patch(MethodInfo original, HarmonyMethod prefix = null, HarmonyMethod postfix = null) {} }
          public static class AccessTools {
            public static MethodInfo Method(Type type, string name) => type.GetMethod(name);
            public static MethodInfo Method(Type type, string name, Type[] argumentTypes) => type.GetMethod(name, argumentTypes);
            public static FieldInfo Field(Type type, string name) => type.GetField(name);
          }
        }
        namespace UnityEngine { public sealed class Object {} }
        namespace ModCheckPlugin {
          [HarmonyLib.HarmonyPatch(typeof(Api), "Removed")]
          public static class RemovedPatch { [HarmonyLib.HarmonyPrefix] public static void BeforeRemoved() {} }
          public static class Plugin {
            public static int ReadWrite() { Api.Count = Api.Removed(); return Api.Count; }
            public static void Calls() { Api.Single(1); Api.RenamedBefore(); Api.Relocated(); Api.BodyOnly(); Api.Stable(); }
            public static int InteropCall() { Il2CppDemo.Api.Count = Il2CppDemo.Api.Removed(); return Il2CppDemo.Api.Count; }
            public static void Reflection() {
              _ = HarmonyLib.AccessTools.Method(typeof(Api), "Removed");
              _ = HarmonyLib.AccessTools.Field(typeof(Api), "Count");
              var harmony = new HarmonyLib.Harmony();
              harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Api), "Single"), postfix: new HarmonyLib.HarmonyMethod(typeof(Plugin).GetMethod(nameof(AfterSingle))));
            }
            public static void AfterSingle() {}
            public static UnityEngine.Object External(UnityEngine.Object value) => value;
          }
        }
        """;

    private const string InteropSource = """
        namespace Il2CppDemo {
          public sealed class Arg { }
          public sealed class MissingType { }
          public static class Api { public static int Removed() => 0; public static int Count; }
        }
        """;

    private const string OtherModSource = """
        namespace OtherMod { public static class Api { public static void Run() {} } }
        """;

    private const string DynamicPatchSource = """
        namespace ModCheckPlugin {
          public static class DynamicPatch {
            public static void Prefix() {}
            public static void Register(System.Reflection.MethodInfo runtimeTarget) =>
              new HarmonyLib.Harmony().Patch(runtimeTarget,
                prefix: new HarmonyLib.HarmonyMethod(typeof(DynamicPatch).GetMethod(nameof(Prefix))));
          }
          [HarmonyLib.HarmonyPatch]
          public static class TargetMethodPatch {
            [HarmonyLib.HarmonyPrefix] public static void Prefix() {}
            public static System.Reflection.MethodBase TargetMethod() => null;
          }
        }
        """;

    private const string MissingSignatureTypeSource = """
        namespace ModCheckPlugin {
          public static class SignatureOnlyDependency {
            public static Il2CppDemo.MissingType MissingField;
          }
        }
        """;

    private const string TypeTokenOnlySource = """
        namespace ModCheckPlugin {
          public static class TokenOnlyDependency {
            public static System.Type MissingTokenOnly() => typeof(Il2CppDemo.MissingType);
            public static System.Type ExistingTokenOnly() => typeof(Demo.Api);
          }
        }
        """;

    private const string OtherModReferenceSource = """
        namespace ModCheckPlugin {
          public static class OtherModDependency {
            public static void CallOther() => OtherMod.Api.Run();
          }
        }
        """;

    private const string ExternalHarmonySource = """
        namespace ModCheckPlugin {
          [HarmonyLib.HarmonyPatch(typeof(OtherMod.Api), "Run")]
          public static class ExternalHarmonyPatch {
            [HarmonyLib.HarmonyPrefix] public static void Prefix() { }
          }
        }
        """;

    private const string InteropReflectionSource = """
        namespace ModCheckPlugin {
          public static class InteropReflectionDependency {
            public static void Reflect() =>
              _ = HarmonyLib.AccessTools.Method(typeof(Il2CppDemo.Api), "ReflectChanged", new[] { typeof(Il2CppDemo.Arg) });
          }
        }
        """;

    private const string ProjectionGameA = """
        namespace Demo {
          public sealed partial class ProjectedState {
            private string StoredValue { get; set; }
            private int Count;
          }
        }
        """;

    private const string ProjectionGameB = """
        namespace Demo {
          public sealed partial class ProjectedState { }
        }
        """;

    private const string ProjectionInterop = """
        namespace Il2CppDemo {
          public sealed partial class ProjectedState {
            public string _StoredValue_k__BackingField { get => null; set { } }
            public int Count { get => 0; set { } }
          }
        }
        """;

    private const string ProjectionMod = """
        namespace InteropProjectionPlugin {
          public static class Plugin {
            public static string ReadBacking(Il2CppDemo.ProjectedState state) => state._StoredValue_k__BackingField;
            public static void WriteBacking(Il2CppDemo.ProjectedState state, string value) => state._StoredValue_k__BackingField = value;
            public static int ReadPlain(Il2CppDemo.ProjectedState state) => state.Count;
            public static void WritePlain(Il2CppDemo.ProjectedState state, int value) => state.Count = value;
          }
        }
        """;

    private const string ProjectionArrayGameA = """
        namespace Demo {
          public sealed class Value { }
          public sealed partial class ProjectedState {
            private Value[] Entries { get; set; }
            private string[] Transform(Value[] entries, int[] scores, System.Collections.Generic.List<Value[]> groups) => null;
          }
        }
        """;

    private const string ProjectionArrayGameB = """
        namespace Demo { public sealed class Value { } }
        """;

    private const string ProjectionArrayInterop = """
        namespace Il2CppInterop.Runtime.InteropTypes.Arrays {
          public sealed class Il2CppReferenceArray<T> { }
          public sealed class Il2CppStructArray<T> { }
          public sealed class Il2CppStringArray { }
        }
        namespace Il2CppSystem.Collections.Generic { public sealed class List<T> { } }
        namespace Il2CppDemo {
          using Il2CppInterop.Runtime.InteropTypes.Arrays;
          public sealed class Value { }
          public sealed partial class ProjectedState {
            public Il2CppReferenceArray<Value> _Entries_k__BackingField { get => null; set { } }
            public Il2CppStringArray Transform(Il2CppReferenceArray<Value> entries, Il2CppStructArray<int> scores,
              Il2CppSystem.Collections.Generic.List<Il2CppReferenceArray<Value>> groups) => null;
          }
        }
        """;

    private const string ProjectionArrayMod = """
        namespace InteropProjectionPlugin {
          using Il2CppInterop.Runtime.InteropTypes.Arrays;
          public static class ArrayPlugin {
            public static Il2CppReferenceArray<Il2CppDemo.Value> ReadArray(Il2CppDemo.ProjectedState state) => state._Entries_k__BackingField;
            public static void WriteArray(Il2CppDemo.ProjectedState state, Il2CppReferenceArray<Il2CppDemo.Value> value) => state._Entries_k__BackingField = value;
            public static Il2CppStringArray CallTransform(Il2CppDemo.ProjectedState state, Il2CppReferenceArray<Il2CppDemo.Value> entries,
              Il2CppStructArray<int> scores, Il2CppSystem.Collections.Generic.List<Il2CppReferenceArray<Il2CppDemo.Value>> groups) =>
                state.Transform(entries, scores, groups);
          }
        }
        """;

    private const string ProjectionPropertyInterop = """
        namespace Il2CppDemo {
          public sealed partial class ProjectedState {
            public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Value> Entries { get => null; set { } }
          }
        }
        """;

    private const string ProjectionPropertyMod = """
        namespace InteropProjectionPlugin {
          using Il2CppInterop.Runtime.InteropTypes.Arrays;
          public static class PropertyPlugin {
            public static Il2CppReferenceArray<Il2CppDemo.Value> ReadProperty(Il2CppDemo.ProjectedState state) => state.Entries;
            public static void WriteProperty(Il2CppDemo.ProjectedState state, Il2CppReferenceArray<Il2CppDemo.Value> value) => state.Entries = value;
          }
        }
        """;

    private const string ProjectionReflectionInterop = """
        namespace HarmonyLib {
          public static class AccessTools {
            public static System.Reflection.MethodInfo PropertyGetter(System.Type type, string name) => type.GetProperty(name)?.GetMethod;
            public static System.Reflection.MethodInfo PropertySetter(System.Type type, string name) => type.GetProperty(name)?.SetMethod;
          }
        }
        """;

    private const string ProjectionReflectionMod = """
        namespace InteropProjectionPlugin {
          public static class ReflectionPlugin {
            public static void ReflectGetter() => _ = HarmonyLib.AccessTools.PropertyGetter(typeof(Il2CppDemo.ProjectedState), "Count");
            public static void ReflectSetter() => _ = HarmonyLib.AccessTools.PropertySetter(typeof(Il2CppDemo.ProjectedState), "Count");
          }
        }
        """;

    private const string ProjectionGetterDecoyGame = """
        namespace Demo {
          public sealed partial class ProjectedState {
            private int get_Count(int value) => value;
          }
        }
        """;

    private const string ProjectionGetterDecoyInterop = """
        namespace Il2CppDemo {
          public sealed partial class ProjectedState {
            public int get_Count(int value) => value;
          }
        }
        """;
}
