using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Fingerprints;

public sealed class SymbolFingerprintService
{
    public IReadOnlyList<IndexFingerprintRecord> Create(
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? methodEvidence = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sourceEvidence = null)
    {
        var result = new List<IndexFingerprintRecord>();
        foreach (var symbol in symbols)
        {
            result.Add(new IndexFingerprintRecord(symbol.SymbolId, "declaration", Hash(symbol.Signature)));
            result.Add(new IndexFingerprintRecord(symbol.SymbolId, "structural", Hash(symbol.Kind + "\n" + symbol.QualifiedName + "\n" + symbol.Signature)));
            if (methodEvidence is not null && methodEvidence.TryGetValue(symbol.SymbolId, out var evidence) && evidence.Count > 0)
                result.Add(new IndexFingerprintRecord(symbol.SymbolId, "method-body", Hash(string.Join("\n", evidence.Order(StringComparer.Ordinal)))));
            if (sourceEvidence is not null && sourceEvidence.TryGetValue(symbol.SymbolId, out var source) && source.Count > 0)
                result.Add(new IndexFingerprintRecord(symbol.SymbolId, "source", Hash(string.Join("\n", source.Order(StringComparer.Ordinal)))));
        }
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
