namespace S1Atlas.Core.Metadata;

/// <summary>
/// Denylist for tool-injected analysis attributes, which describe the extraction
/// (native addresses, metadata tokens, field offsets) rather than the game and must
/// never appear as game attributes. Confirmed names, verified in AT-69 Task 1:
/// <list type="bullet">
/// <item><c>Cpp2ILInjected.AddressAttribute</c>, <c>Cpp2ILInjected.TokenAttribute</c>,
/// and <c>Cpp2ILInjected.FieldOffsetAttribute</c>, confirmed empirically by read-only
/// enumeration of <c>CustomAttributes</c> over the attributed Cpp2IL reconstruction in
/// this environment (146,033 / 287,610 / 58,087 uses across one extraction) and by the
/// Cpp2IL upstream <c>attributeinjector</c> processing layer.</item>
/// <item><c>Cpp2ILInjected.AttributeAttribute</c>, confirmed in the Cpp2IL upstream
/// <c>attributeinjector</c> layer (pinned release 2022.1.0-pre-release.21 and
/// development); not observed in local reconstructions.</item>
/// <item><c>Cpp2ILInjected.CallsAttribute</c>, named by the ticket (AT-59's
/// <c>[Calls]</c>); not observed in local reconstructions or Cpp2IL upstream, denied
/// here under the confirmed injected namespace.</item>
/// </list>
/// The <c>Cpp2ILInjected.</c> namespace prefix additionally covers any future injected
/// attribute. Matching is ordinal: metadata type names are case-sensitive.
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
