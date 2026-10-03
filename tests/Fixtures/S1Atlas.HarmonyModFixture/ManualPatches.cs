// Manual harmony.Patch call cases: constant forms resolve, non-constant forms stay unresolved.
using Game;
using HarmonyLib;

namespace Mod;

public class ManualPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Run"),
            prefix: new HarmonyMethod(typeof(ManualPatch), nameof(ManualPrefix)));
    }

    public static bool ManualPrefix() => true;
}

public class ManualOverloadPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Compute", new Type[] { typeof(int), typeof(string) }),
            postfix: new HarmonyMethod(typeof(ManualOverloadPatch), nameof(ManualPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(Widget), HelperName()),
            prefix: new HarmonyMethod(typeof(ManualOverloadPatch), nameof(ManualPostfix)));
    }

    public static void ManualPostfix()
    {
    }

    private static string HelperName() => "Run";
}

public class ManualEmptyTypesPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Run", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(ManualEmptyTypesPatch), nameof(EmptyPrefix)));
    }

    public static void EmptyPrefix()
    {
    }
}

public class ManualAmbiguousMethodPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Run"),
            prefix: new HarmonyMethod(typeof(ManualAmbiguousMethodPatch), "Do"));
    }

    public static void Do(int x)
    {
    }

    public static void Do(string s)
    {
    }
}

public class ManualUnknownMethodPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Run"),
            prefix: new HarmonyMethod(typeof(ManualUnknownMethodPatch), "Missing"));
    }
}

public class ManualNonConstantMethodPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Untouched"),
            prefix: GetPatch());
    }

    public static void Worker()
    {
    }

    private static HarmonyMethod GetPatch() => new HarmonyMethod(typeof(ManualNonConstantMethodPatch), nameof(Worker));
}

public class ManualBranchPatch
{
    public static void Install(Harmony harmony, bool flag)
    {
        var original = flag
            ? AccessTools.Method(typeof(Widget), "Run")
            : AccessTools.Method(typeof(Widget), "Untouched");
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(ManualBranchPatch), nameof(Branched)));
    }

    public static void Branched()
    {
    }
}

public class ManualMethodInfoPatch
{
    public static void Install(Harmony harmony)
    {
        var info = AccessTools.Method(typeof(ManualMethodInfoPatch), nameof(InfoPrefix));
        harmony.Patch(AccessTools.Method(typeof(Widget), "Run"), prefix: new HarmonyMethod(info!));
    }

    public static void InfoPrefix()
    {
    }
}

public class ManualOverloadDisambiguationPatch
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Widget), "Run"),
            prefix: new HarmonyMethod(typeof(ManualOverloadDisambiguationPatch), "Do", new Type[] { typeof(int) }));
    }

    public static void Do(int x)
    {
    }

    public static void Do(string s)
    {
    }
}

public class ManualNewobjPrecisionPatch
{
    public static void Install(Harmony harmony, string name)
    {
        var local = new Harmony("review-m5");
        local.Patch(
            AccessTools.Method(typeof(Widget), name),
            prefix: new HarmonyMethod(typeof(ManualNewobjPrecisionPatch), nameof(NewobjPrefix)));
    }

    public static void NewobjPrefix()
    {
    }
}

public class ManualFloatConstantPatch
{
    public static float Scale;

    public static double Factor;

    public static void Install(Harmony harmony)
    {
        Scale = 1.5f;
        var target = AccessTools.Method(typeof(Widget), "Run");
        Factor = 2.0;
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(ManualFloatConstantPatch), nameof(FloatPrefix)));
    }

    public static void FloatPrefix()
    {
    }
}
