using System.Globalization;

namespace S1Atlas.Core.Metadata;

/// <summary>
/// Canonical single-string rendering of a decoded attribute argument value. Drives
/// display and <c>--arg</c> matching: enums render as their underlying numeric value,
/// never the member name, and arrays flatten to comma-joined element renderings.
/// </summary>
public static class CustomAttributeRendering
{
    public static string CanonicalValue(CustomAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Kind switch
        {
            CustomAttributeValueKind.Null => "null",
            CustomAttributeValueKind.String => (string?)value.Value ?? "null",
            CustomAttributeValueKind.Primitive => RenderPrimitive(value.Value),
            CustomAttributeValueKind.Type => value.TypeName ?? "null",
            CustomAttributeValueKind.Enum => RenderNumeric(value.Value),
            CustomAttributeValueKind.Array => value.Elements is null
                ? string.Empty
                : string.Join(",", value.Elements.Select(CanonicalValue)),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown attribute value kind."),
        };
    }

    private static string RenderPrimitive(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        _ => RenderNumeric(value),
    };

    private static string RenderNumeric(object? value) =>
        value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
