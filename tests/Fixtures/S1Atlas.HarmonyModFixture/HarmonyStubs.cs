// Stub HarmonyLib shapes for patch-target tests. Written from scratch to mirror the
// public attribute and call shapes (matched by full type name); no Harmony package.
namespace HarmonyLib;

public enum MethodType
{
    Normal,
    Getter,
    Setter,
    Constructor,
    StaticConstructor,
    Enumerator,
    Async
}

public enum ArgumentType
{
    Normal,
    Ref,
    Out,
    Pointer
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class HarmonyPatch : Attribute
{
    public HarmonyPatch()
    {
    }

    public HarmonyPatch(Type declaringType)
    {
    }

    public HarmonyPatch(Type declaringType, Type[] argumentTypes)
    {
    }

    public HarmonyPatch(Type declaringType, string methodName)
    {
    }

    public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes)
    {
    }

    public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
    }

    public HarmonyPatch(Type declaringType, MethodType methodType)
    {
    }

    public HarmonyPatch(Type declaringType, MethodType methodType, params Type[] argumentTypes)
    {
    }

    public HarmonyPatch(Type declaringType, MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
    }

    public HarmonyPatch(Type declaringType, string methodName, MethodType methodType)
    {
    }

    public HarmonyPatch(string methodName)
    {
    }

    public HarmonyPatch(string methodName, params Type[] argumentTypes)
    {
    }

    public HarmonyPatch(string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
    }

    public HarmonyPatch(string methodName, MethodType methodType)
    {
    }

    public HarmonyPatch(MethodType methodType)
    {
    }

    public HarmonyPatch(MethodType methodType, params Type[] argumentTypes)
    {
    }

    public HarmonyPatch(MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
    }

    public HarmonyPatch(Type[] argumentTypes)
    {
    }

    public HarmonyPatch(Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
    }

    public HarmonyPatch(string typeName, string methodName, MethodType methodType = MethodType.Normal)
    {
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPrefix : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPostfix : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyTranspiler : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyFinalizer : Attribute
{
}

public class HarmonyMethod
{
    public HarmonyMethod()
    {
    }

    public HarmonyMethod(Type declaringType, string methodName)
    {
    }

    public HarmonyMethod(Type declaringType, string methodName, Type[]? argumentTypes)
    {
    }

    public HarmonyMethod(System.Reflection.MethodInfo method)
    {
    }
}

public class Harmony
{
    public Harmony(string id)
    {
    }

    public void Patch(
        System.Reflection.MethodBase? original,
        HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null,
        HarmonyMethod? finalizer = null)
    {
    }
}

public static class AccessTools
{
    public static System.Reflection.MethodInfo? Method(Type type, string name) => null;

    public static System.Reflection.MethodInfo? Method(Type type, string name, Type[]? typeArguments) => null;
}
