using Game;
using HarmonyLib;
using System.Reflection;

namespace Mod;

public static class ReflectionLookups
{
    public static void AccessMethod() => _ = AccessTools.Method(typeof(Widget), "Run");
    public static void AccessMethodOverload() => _ = AccessTools.Method(typeof(Widget), "Compute", new[] { typeof(int), typeof(string) });
    public static void AccessMethodFourArguments() => _ = AccessTools.Method(typeof(Widget), "Compute", new[] { typeof(int) }, null);
    public static void AccessField() => _ = AccessTools.Field(typeof(Widget), "Count");
    public static void AccessProperty() => _ = AccessTools.Property(typeof(Widget), "Name");
    public static void AccessGetter() => _ = AccessTools.PropertyGetter(typeof(Widget), "Name");
    public static void AccessSetter() => _ = AccessTools.PropertySetter(typeof(Widget), "Name");
    public static void AccessConstructor() => _ = AccessTools.Constructor(typeof(Widget), new[] { typeof(int) });
    public static void AccessEmptyConstructor() => _ = AccessTools.Constructor(typeof(Widget), Type.EmptyTypes);
    public static void AccessConstructorWithFlag() => _ = AccessTools.Constructor(typeof(Widget), new[] { typeof(int) }, false);
    public static void AccessStaticConstructor() => _ = AccessTools.Constructor(typeof(Widget), Type.EmptyTypes, true);
    public static void TypeMethod() => _ = typeof(Widget).GetMethod("Run");
    public static void TypeMethodOverload() => _ = typeof(Widget).GetMethod("Compute", new[] { typeof(int) });
    public static void TypeField() => _ = typeof(Widget).GetField("Count");
    public static void TypeProperty() => _ = typeof(Widget).GetProperty("Name");
    public static void TypeMethodWithFlags() => _ = typeof(Widget).GetMethod("Run", BindingFlags.Public | BindingFlags.Instance);
    public static void TypeFieldWithFlags() => _ = typeof(Widget).GetField("Count", BindingFlags.Public | BindingFlags.Instance);
    public static void TypePropertyWithFlags() => _ = typeof(Widget).GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);

    public static void DynamicName(string name) => _ = AccessTools.Method(typeof(Widget), name);
    public static void IncompleteArray() => _ = AccessTools.Constructor(typeof(Widget), new Type[1]);

    public static void BranchName(bool first)
    {
        var name = first ? "Run" : "Compute";
        _ = AccessTools.Method(typeof(Widget), name);
    }

    public static void BranchType(bool first)
    {
        var type = first ? typeof(Widget) : typeof(string);
        _ = type.GetMethod("Run");
    }

    public static void BranchConstructor(bool first)
    {
        var types = first ? new[] { typeof(int) } : new[] { typeof(string) };
        _ = AccessTools.Constructor(typeof(Widget), types);
    }

    public static void DynamicTypeMethodArguments(Type[] types) => _ = typeof(Widget).GetMethod("Compute", types);
}
