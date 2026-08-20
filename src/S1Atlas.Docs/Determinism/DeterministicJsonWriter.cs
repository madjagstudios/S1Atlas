using System.Text.Json;
using S1Atlas.Docs.Generation;

namespace S1Atlas.Docs.Determinism;

public sealed class DeterministicJsonWriter
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    public string WriteSearchIndexJson(IReadOnlyList<PortalSymbolModel> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var entries = symbols
            .Select(symbol => new SearchEntry(
                symbol.Codebase.ToString(),
                symbol.Channel.ToString(),
                symbol.QualifiedName,
                symbol.Signature,
                symbol.Kind.ToString(),
                symbol.SymbolId,
                symbol.CanonicalKey,
                symbol.PagePath + (string.IsNullOrEmpty(symbol.Anchor) ? string.Empty : "#" + symbol.Anchor)))
            .OrderBy(entry => entry.Codebase, StringComparer.Ordinal)
            .ThenBy(entry => entry.Channel, StringComparer.Ordinal)
            .ThenBy(entry => entry.QualifiedName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Signature, StringComparer.Ordinal)
            .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.SymbolId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Href, StringComparer.Ordinal)
            .ToArray();
        return Normalize(JsonSerializer.Serialize(entries, _options));
    }

    public string WriteInlineSearchIndexJavaScript(IReadOnlyList<PortalSymbolModel> symbols) =>
        "const S1ATLAS_SEARCH_INDEX = Object.freeze(" + WriteSearchIndexJson(symbols) + ");\n";

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') + "\n";

    private sealed record SearchEntry(
        string Codebase,
        string Channel,
        string QualifiedName,
        string Signature,
        string Kind,
        string SymbolId,
        string ExactKey,
        string Href);
}
