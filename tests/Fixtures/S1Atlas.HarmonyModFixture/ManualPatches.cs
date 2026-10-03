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
