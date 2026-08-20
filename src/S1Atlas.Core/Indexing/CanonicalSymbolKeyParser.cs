namespace S1Atlas.Core.Indexing;

public static class CanonicalSymbolKeyParser
{
    public static string NamespaceFrom(string canonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        var parts = canonicalKey.Split(':', 4, StringSplitOptions.None);
        if (parts.Length < 4) return string.Empty;
        var symbolKey = parts[3];
        var separator = symbolKey.IndexOf("::", StringComparison.Ordinal);
        var name = separator >= 0 ? symbolKey[..separator] : symbolKey;
        var lastDot = name.LastIndexOf('.');
        return lastDot > 0 ? name[..lastDot] : string.Empty;
    }
}
