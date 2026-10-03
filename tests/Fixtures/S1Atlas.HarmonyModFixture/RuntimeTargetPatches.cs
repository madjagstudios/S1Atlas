// Runtime-computed targets: reported unresolved, never guessed.
using HarmonyLib;

namespace Mod;

[HarmonyPatch]
public class TargetMethodPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }

    public static System.Reflection.MethodBase? TargetMethod() => null;
}

[HarmonyPatch]
public class TargetMethodsPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
    }

    public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase>? TargetMethods() => null;
}
