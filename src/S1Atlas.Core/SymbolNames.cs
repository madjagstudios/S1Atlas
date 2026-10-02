namespace S1Atlas.Core;

/// <summary>
/// Shared qualified-name parsing for symbol display and matching.
/// </summary>
public static class SymbolNames
{
    /// <summary>
    /// Returns the type or member identifier from a qualified name. Members take
    /// the text after the last "::" cut at the first '(' or ':'; typed field,
    /// property, and event shapes carry "Type Name" there, so the name after the
    /// last space wins. Types take the text after the last '.' or '+'. Generic
    /// arity markers and compiler-generated names pass through unchanged.
    /// </summary>
    public static string SimpleName(string qualifiedName)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);

        var member = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        if (member >= 0)
        {
            var tail = qualifiedName[(member + 2)..];
            var cut = tail.IndexOfAny(['(', ':']);
            if (cut >= 0)
            {
                tail = tail[..cut];
            }

            var space = tail.LastIndexOf(' ');
            return space >= 0 ? tail[(space + 1)..] : tail;
        }

        var separator = qualifiedName.LastIndexOfAny(['.', '+']);
        return separator >= 0 ? qualifiedName[(separator + 1)..] : qualifiedName;
    }
}
