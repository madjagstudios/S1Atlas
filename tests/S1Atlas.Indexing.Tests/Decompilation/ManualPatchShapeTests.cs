using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

/// <summary>
/// AT-136: manual Harmony registrations in the shapes real mods use, compiled to real
/// IL and read back through the decompiler. The stubs mirror HarmonyX's signatures
/// (four-argument AccessTools.Method, six-argument Harmony.Patch returning MethodInfo).
/// </summary>
public sealed class ManualPatchShapeTests
{
    [Fact]
    public async Task Guarded_constant_targets_and_property_setters_resolve()
    {
        var decompilation = await CompileAndDecompileAsync(GuardedTargetsSource, TestContext.Current.CancellationToken);

        var custody = Patches(decompilation, "Mod.PoliceCustodyPatch", "Apply");
        Assert.Collection(custody.OrderBy(patch => patch.Kind),
            prefix => AssertResolved(prefix, HarmonyPatchKind.Prefix,
                "Game.Player::RpcLogic___Arrest_Server_2166136261()", "Mod.PoliceCustodyPatch", "Prefix"),
            postfix => AssertResolved(postfix, HarmonyPatchKind.Postfix,
                "Game.Player::RpcLogic___Arrest_Server_2166136261()", "Mod.PoliceCustodyPatch", "Postfix"));

        var lockdown = Patches(decompilation, "Mod.Release1LockdownGatePatch", "Apply");
        Assert.Collection(lockdown.OrderBy(patch => patch.TargetSignature, StringComparer.Ordinal),
            tick => AssertResolved(tick, HarmonyPatchKind.Prefix,
                "Game.CurfewManager::OnUncappedMinPass", "Mod.Release1LockdownGatePatch", "TickPrefix"),
            setter => AssertResolved(setter, HarmonyPatchKind.Prefix,
                "Game.CurfewManager::set_IsCurrentlyActive", "Mod.Release1LockdownGatePatch", "SetterPrefix"));

        // A local written on two paths has no single value after the merge.
        var twoStores = Assert.Single(Patches(decompilation, "Mod.TwoStorePatch", "Apply"));
        Assert.Null(twoStores.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.UnrecognizedManualShape, twoStores.Reason);
    }

    [Fact]
    public async Task Returning_target_helpers_resolve_only_a_single_constant_return_target()
    {
        var decompilation = await CompileAndDecompileAsync(ReturningTargetsSource, TestContext.Current.CancellationToken);
        const string custody = "Mod.PoliceCustodyPatch";
        Assert.Collection(Patches(decompilation, custody, "Apply").OrderBy(patch => patch.Kind),
            prefix => AssertResolved(prefix, HarmonyPatchKind.Prefix,
                "Game.Player::RpcLogic___Arrest_Server_2166136261()", custody, "Prefix"),
            postfix => AssertResolved(postfix, HarmonyPatchKind.Postfix,
                "Game.Player::RpcLogic___Arrest_Server_2166136261()", custody, "Postfix"));
        Assert.Empty(Patches(decompilation, "Mod.Plugin", "Initialize"));

        // Different returns and a second helper level cannot supply a certain target.
        Assert.Null(Assert.Single(Patches(decompilation, custody, "ApplyAmbiguous")).TargetSignature);
        Assert.Null(Assert.Single(Patches(decompilation, custody, "ApplyNested")).TargetSignature);
    }

    [Fact]
    public async Task Null_coalescing_throw_targets_resolve_at_the_registering_method()
    {
        var decompilation = await CompileAndDecompileAsync(CoalescingTargetsSource, TestContext.Current.CancellationToken);
        const string lockdown = "Mod.Release1LockdownGatePatch";
        AssertResolved(Assert.Single(Patches(decompilation, lockdown, "EnsurePatched")),
            HarmonyPatchKind.Postfix, "Game.CurfewManager::OnUncappedMinPass", lockdown, "Postfix");
        Assert.Empty(Patches(decompilation, lockdown, "TryEngage"));

        // A null fallback that continues with another target still has two possibilities.
        Assert.Null(Assert.Single(Patches(decompilation, lockdown, "ApplyFallback")).TargetSignature);
        // A throwing arm can register its own patch, which must not be skipped.
        Assert.Collection(Patches(decompilation, lockdown, "ApplyThrowingPatch"),
            throwing => AssertResolved(throwing, HarmonyPatchKind.Postfix, "Game.NPCHealth::KnockOut", lockdown, "Postfix"),
            continuation => Assert.Null(continuation.TargetSignature));
    }

    [Fact]
    public async Task Forwarding_helpers_report_targets_at_their_call_sites()
    {
        var decompilation = await CompileAndDecompileAsync(HelperSource, TestContext.Current.CancellationToken);
        const string arthur = "Mod.Chapter3ArthurResponsePatch";

        Assert.Collection(Patches(decompilation, arthur, "Apply").OrderBy(patch => patch.TargetSignature, StringComparer.Ordinal),
            patch => AssertResolved(patch, HarmonyPatchKind.Prefix, "Game.NPC::ReceiveImpact", arthur, "ReceiveImpactPrefix"),
            patch => AssertResolved(patch, HarmonyPatchKind.Postfix, "Game.NPCHealth::Die", arthur, "HealthPostfix"),
            patch => AssertResolved(patch, HarmonyPatchKind.Postfix, "Game.NPCHealth::KnockOut", arthur, "HealthPostfix"),
            patch => AssertResolved(patch, HarmonyPatchKind.Postfix, "Game.NPCHealth::TakeDamage", arthur, "HealthPostfix"),
            patch => AssertResolved(patch, HarmonyPatchKind.Prefix, "Game.NPCResponses::ImpactReceived", arthur, "ImpactReceivedPrefix"));

        // A non-constant argument stays unresolved, reported at the caller.
        var dynamic = Assert.Single(Patches(decompilation, arthur, "ApplyDynamic"));
        Assert.Equal(HarmonyPatchKind.Prefix, dynamic.Kind);
        Assert.Null(dynamic.TargetSignature);
        Assert.NotNull(dynamic.Reason);
        Assert.Equal("ReceiveImpactPrefix", dynamic.PatchMethodName);

        // Inlined helpers no longer report "some caller's values" themselves.
        Assert.Empty(Patches(decompilation, arthur, "PatchPrefix"));
        Assert.Empty(Patches(decompilation, arthur, "PatchPostfix"));

        // A method that patches constants on its own is not a forwarding helper, and
        // inlining is one level deep, so the entry point gains nothing.
        AssertResolved(Assert.Single(Patches(decompilation, "Mod.SelfContainedPatch", "Apply")),
            HarmonyPatchKind.Prefix, "Game.NPC::ReceiveImpact", "Mod.SelfContainedPatch", "Prefix");
        Assert.Empty(Patches(decompilation, "Mod.Plugin", "Initialize"));
    }

    private static async Task<ManagedDecompilation> CompileAndDecompileAsync(string source, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-manual-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ManualPatchShapes.dll");
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(reference => MetadataReference.CreateFromFile(reference));
            var compilation = CSharpCompilation.Create(
                "ManualPatchShapes",
                [CSharpSyntaxTree.ParseText(HarmonyStubs), CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var output = File.Create(path))
            {
                var result = compilation.Emit(output);
                Assert.True(result.Success, string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }

            return await new IlSpyManagedDecompiler().DecompileAsync(path, ct);
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    private static IReadOnlyList<ManagedPatchFact> Patches(ManagedDecompilation decompilation, string type, string member) =>
        decompilation.Types.Single(candidate => candidate.FullName == type)
            .Members.Single(candidate => candidate.Name == member)
            .PatchesOrEmpty;

    private static void AssertResolved(
        ManagedPatchFact fact, HarmonyPatchKind kind, string target, string patchType, string patchMethod)
    {
        Assert.Equal(kind, fact.Kind);
        Assert.Equal(target, fact.TargetSignature);
        Assert.Null(fact.Reason);
        Assert.Equal(RelationshipEvidence.RecoveredIL, fact.Evidence);
        Assert.Equal(patchType, fact.PatchMethodType);
        Assert.Equal(patchMethod, fact.PatchMethodName);
    }

    private const string HarmonyStubs = """
        namespace HarmonyLib
        {
            using System;
            using System.Reflection;

            public class HarmonyMethod
            {
                public HarmonyMethod(MethodInfo method) { }
                public HarmonyMethod(Type methodType, string methodName, Type[] argumentTypes = null) { }
            }

            public class Harmony
            {
                public Harmony(string id) { }
                public MethodInfo Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
                    HarmonyMethod transpiler = null, HarmonyMethod finalizer = null, HarmonyMethod ilmanipulator = null) => null;
            }

            public static class AccessTools
            {
                public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null) => null;
                public static MethodInfo PropertyGetter(Type type, string name) => null;
                public static MethodInfo PropertySetter(Type type, string name) => null;
            }
        }

        namespace Game
        {
            public class Player { public void RpcLogic___Arrest_Server_2166136261() { } }
            public class CurfewManager
            {
                public bool IsCurrentlyActive { get; set; }
                public void OnUncappedMinPass() { }
            }
            public class NPC { public void ReceiveImpact() { } }
            public class NPCResponses { public void ImpactReceived() { } }
            public class NPCHealth
            {
                public void TakeDamage() { }
                public void KnockOut() { }
                public void Die() { }
            }
        }

        namespace Mod
        {
            public sealed class Logger { public void Warn(string message) { } }
        }
        """;

    private const string HelperSource = """
        namespace Mod
        {
            using System;
            using System.Reflection;
            using HarmonyLib;

            public static class Chapter3ArthurResponsePatch
            {
                public static void Apply(Harmony harmony, Logger log)
                {
                    PatchPrefix(harmony, log, typeof(Game.NPC), nameof(Game.NPC.ReceiveImpact), nameof(ReceiveImpactPrefix));
                    PatchPrefix(harmony, log, typeof(Game.NPCResponses), nameof(Game.NPCResponses.ImpactReceived), nameof(ImpactReceivedPrefix));
                    var postfix = AccessTools.Method(typeof(Chapter3ArthurResponsePatch), nameof(HealthPostfix));
                    PatchPostfix(harmony, log, typeof(Game.NPCHealth), nameof(Game.NPCHealth.TakeDamage), postfix);
                    PatchPostfix(harmony, log, typeof(Game.NPCHealth), nameof(Game.NPCHealth.KnockOut), postfix);
                    PatchPostfix(harmony, log, typeof(Game.NPCHealth), nameof(Game.NPCHealth.Die), postfix);
                }

                public static void ApplyDynamic(Harmony harmony, Logger log, string name)
                {
                    PatchPrefix(harmony, log, typeof(Game.NPC), name, nameof(ReceiveImpactPrefix));
                }

                private static void PatchPrefix(Harmony harmony, Logger log, Type type, string methodName, string prefixName)
                {
                    var target = AccessTools.Method(type, methodName);
                    if (target == null)
                    {
                        log.Warn("Missing " + type.Name + "." + methodName);
                        return;
                    }

                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(Chapter3ArthurResponsePatch), prefixName));
                }

                private static void PatchPostfix(Harmony harmony, Logger log, Type type, string methodName, MethodInfo postfix)
                {
                    var target = AccessTools.Method(type, methodName);
                    if (target == null)
                    {
                        log.Warn("Missing " + type.Name + "." + methodName);
                        return;
                    }

                    harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                }

                public static bool ReceiveImpactPrefix() => true;
                public static bool ImpactReceivedPrefix() => true;
                public static void HealthPostfix() { }
            }

            public static class SelfContainedPatch
            {
                public static void Apply(Harmony harmony)
                {
                    harmony.Patch(AccessTools.Method(typeof(Game.NPC), "ReceiveImpact"),
                        prefix: new HarmonyMethod(typeof(SelfContainedPatch), nameof(Prefix)));
                }

                public static bool Prefix() => true;
            }

            public static class Plugin
            {
                public static void Initialize(Harmony harmony, Logger log)
                {
                    Chapter3ArthurResponsePatch.Apply(harmony, log);
                    SelfContainedPatch.Apply(harmony);
                }
            }
        }
        """;

    private const string ReturningTargetsSource = """
        namespace Mod
        {
            using System;
            using System.Reflection;
            using HarmonyLib;

            public static class PoliceCustodyPatch
            {
                public static bool Alternate;

                private static MethodInfo ResolveTarget()
                {
                    var target = AccessTools.Method(typeof(Game.Player), "RpcLogic___Arrest_Server_2166136261", Type.EmptyTypes);
                    if (target == null || target.ReturnType != typeof(void) || target.DeclaringType != typeof(Game.Player))
                        throw new MissingMethodException(typeof(Game.Player).FullName, "RpcLogic___Arrest_Server_2166136261");
                    return target;
                }

                public static void Apply(Harmony harmony)
                {
                    var target = ResolveTarget();
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(PoliceCustodyPatch), nameof(Prefix)),
                        postfix: new HarmonyMethod(typeof(PoliceCustodyPatch), nameof(Postfix)));
                }

                private static MethodInfo AmbiguousTarget()
                {
                    if (Alternate)
                        return AccessTools.Method(typeof(Game.NPCHealth), "Die");
                    return AccessTools.Method(typeof(Game.NPCHealth), "KnockOut");
                }

                private static MethodInfo NestedTarget() => ResolveTarget();
                public static void ApplyAmbiguous(Harmony harmony) =>
                    harmony.Patch(AmbiguousTarget(), prefix: new HarmonyMethod(typeof(PoliceCustodyPatch), nameof(Prefix)));
                public static void ApplyNested(Harmony harmony) =>
                    harmony.Patch(NestedTarget(), prefix: new HarmonyMethod(typeof(PoliceCustodyPatch), nameof(Prefix)));
                public static void Prefix() { }
                public static void Postfix() { }
            }

            public static class Plugin
            {
                public static void Initialize(Harmony harmony) => PoliceCustodyPatch.Apply(harmony);
            }
        }
        """;

    private const string CoalescingTargetsSource = """
        namespace Mod
        {
            using System;
            using HarmonyLib;

            public static class Release1LockdownGatePatch
            {
                private static bool _patched;
                public static bool TryEngage(Harmony harmony) => EnsurePatched(harmony);

                private static bool EnsurePatched(Harmony harmony)
                {
                    if (_patched)
                        return true;
                    try
                    {
                        var target = AccessTools.Method(typeof(Game.CurfewManager), "OnUncappedMinPass")
                            ?? throw new MissingMethodException(typeof(Game.CurfewManager).FullName, "OnUncappedMinPass");
                        var postfix = AccessTools.Method(typeof(Release1LockdownGatePatch), nameof(Postfix))
                            ?? throw new MissingMethodException(typeof(Release1LockdownGatePatch).FullName, nameof(Postfix));
                        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                        _patched = true;
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                public static void ApplyFallback(Harmony harmony)
                {
                    var target = AccessTools.Method(typeof(Game.NPCHealth), "Die")
                        ?? AccessTools.Method(typeof(Game.NPCHealth), "KnockOut");
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(Release1LockdownGatePatch), nameof(Postfix)));
                }

                public static void ApplyThrowingPatch(Harmony harmony)
                {
                    var target = AccessTools.Method(typeof(Game.NPCHealth), "Die")
                        ?? throw new InvalidOperationException(harmony.Patch(
                            AccessTools.Method(typeof(Game.NPCHealth), "KnockOut"),
                            postfix: new HarmonyMethod(typeof(Release1LockdownGatePatch), nameof(Postfix))).Name);
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(Release1LockdownGatePatch), nameof(Postfix)));
                }

                public static void Postfix() { }
            }
        }
        """;

    private const string GuardedTargetsSource = """
        namespace Mod
        {
            using System;
            using System.Reflection;
            using HarmonyLib;

            public static class PoliceCustodyPatch
            {
                private const string TargetMethodName = "RpcLogic___Arrest_Server_2166136261";

                public static void Apply(Harmony harmony, Logger log)
                {
                    var target = AccessTools.Method(typeof(Game.Player), TargetMethodName, Type.EmptyTypes);
                    if (target == null)
                    {
                        log.Warn("Missing " + TargetMethodName);
                        return;
                    }

                    harmony.Patch(target,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(PoliceCustodyPatch), nameof(Prefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(PoliceCustodyPatch), nameof(Postfix))));
                }

                public static bool Prefix() => true;
                public static void Postfix() { }
            }

            public static class Release1LockdownGatePatch
            {
                public static void Apply(Harmony harmony)
                {
                    try
                    {
                        var tick = AccessTools.Method(typeof(Game.CurfewManager), "OnUncappedMinPass");
                        if (tick != null)
                            harmony.Patch(tick, prefix: new HarmonyMethod(AccessTools.Method(typeof(Release1LockdownGatePatch), nameof(TickPrefix))));
                        var setter = AccessTools.PropertySetter(typeof(Game.CurfewManager), nameof(Game.CurfewManager.IsCurrentlyActive));
                        if (setter != null)
                            harmony.Patch(setter, prefix: new HarmonyMethod(AccessTools.Method(typeof(Release1LockdownGatePatch), nameof(SetterPrefix))));
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(exception.Message);
                    }
                }

                public static bool TickPrefix() => true;
                public static bool SetterPrefix() => true;
            }

            public static class TwoStorePatch
            {
                public static void Apply(Harmony harmony, bool flag)
                {
                    MethodInfo target = AccessTools.Method(typeof(Game.Player), "A");
                    if (flag)
                        target = AccessTools.Method(typeof(Game.Player), "B");
                    harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(TwoStorePatch), nameof(Prefix))));
                }

                public static void Prefix() { }
            }
        }
        """;
}
