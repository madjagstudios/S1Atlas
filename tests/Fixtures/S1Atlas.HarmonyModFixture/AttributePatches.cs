// Attribute-declared patch cases. Each class covers one HarmonyPatch combination.
using Game;
using HarmonyLib;

namespace Mod;

[HarmonyPatch(typeof(Widget), "Run")]
public class RunPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => true;
}

[HarmonyPatch(typeof(Widget))]
public class ComputePatch
{
    [HarmonyPatch("Compute", typeof(int), typeof(string))]
    [HarmonyPostfix]
    public static void Postfix()
    {
    }
}

[HarmonyPatch(typeof(Widget), "Name", MethodType.Getter)]
public class NameGetterPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget))]
public class NameSetterPatch
{
    [HarmonyPatch("Name", MethodType.Setter)]
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget), MethodType.Constructor)]
public class CtorPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
    }
}

[HarmonyPatch(typeof(Widget), MethodType.StaticConstructor)]
public class StaticCtorPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget), "Compute", new Type[] { typeof(int) }, new ArgumentType[] { ArgumentType.Ref })]
public class RefOverloadPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget), "Consume", new Type[] { typeof(int) }, new ArgumentType[] { ArgumentType.Out })]
public class OutOverloadPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget.Nested), "Inner")]
public class ConventionPatch
{
    public static void Transpiler()
    {
    }
}

[HarmonyPatch(typeof(Widget), "Compute")]
public class AmbiguousPatch
{
    public static void Finalizer()
    {
    }
}

[HarmonyPatch("Game.Widget", "Run")]
public class StringNamePatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

[HarmonyPatch(typeof(Widget), "Run", MethodType.Enumerator)]
public class EnumeratorPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
    }
}

public class LonelyPatch
{
    public static void Prefix()
    {
    }
}
