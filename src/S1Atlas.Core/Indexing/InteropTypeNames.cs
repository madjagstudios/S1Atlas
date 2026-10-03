namespace S1Atlas.Core.Indexing;

/// <summary>
/// Normalizes generated Il2Cpp interop type names back to game type names. The
/// generator prefixes the first namespace segment with "Il2Cpp"
/// ("ScheduleOne.X" becomes "Il2CppScheduleOne.X") and places global-namespace
/// types under the "Il2Cpp" namespace ("ActionList" becomes "Il2Cpp.ActionList").
/// The "Il2CppInterop" runtime namespace is real managed code, not generated, and
/// is left unchanged.
/// </summary>
public static class InteropTypeNames
{
    public static string Normalize(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        var dot = typeName.IndexOf('.');
        if (dot < 0)
            return typeName;

        var first = typeName[..dot];
        if (string.Equals(first, "Il2Cpp", StringComparison.Ordinal))
            return typeName[(dot + 1)..];
        if (string.Equals(first, "Il2CppInterop", StringComparison.Ordinal))
            return typeName;
        if (first.StartsWith("Il2Cpp", StringComparison.Ordinal))
            return first["Il2Cpp".Length..] + typeName[dot..];

        return typeName;
    }
}
