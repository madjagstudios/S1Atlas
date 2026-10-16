namespace S1Atlas.Core.Metadata;

/// <summary>
/// Denylist for the attributes Cpp2IL injects into its reconstruction. They describe
/// the extraction (native addresses, metadata tokens, field offsets), not the game, so
/// they are never reported as game attributes.
/// <list type="bullet">
/// <item><c>Address</c>, <c>Token</c> and <c>FieldOffset</c> appear throughout an
/// attributed reconstruction of the game.</item>
/// <item><c>Attribute</c> comes from Cpp2IL's attribute injector but has not been seen
/// in a reconstruction.</item>
/// <item><c>Calls</c> has not been seen either. It is listed explicitly because older
/// analysis output referred to <c>[Calls]</c>.</item>
/// </list>
/// The <c>Cpp2ILInjected.</c> prefix also covers anything Cpp2IL injects later.
/// Matching is ordinal, since metadata type names are case-sensitive.
/// </summary>
public static class AnalysisAttributeFilter
{
    private static readonly HashSet<string> DeniedTypeNames = new(StringComparer.Ordinal)
    {
        "Cpp2ILInjected.AddressAttribute",
        "Cpp2ILInjected.TokenAttribute",
        "Cpp2ILInjected.FieldOffsetAttribute",
        "Cpp2ILInjected.AttributeAttribute",
        "Cpp2ILInjected.CallsAttribute",
    };

    private static readonly string[] DeniedNamespacePrefixes = ["Cpp2ILInjected."];

    public static bool IsAnalysisAttribute(string attributeTypeFullName)
    {
        if (string.IsNullOrEmpty(attributeTypeFullName))
            return false;
        if (DeniedTypeNames.Contains(attributeTypeFullName))
            return true;
        return DeniedNamespacePrefixes.Any(prefix =>
            attributeTypeFullName.StartsWith(prefix, StringComparison.Ordinal));
    }
}
