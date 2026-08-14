using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Core.Indexing;

public sealed record SymbolIdentity(
    CodebaseKind Codebase,
    CodeChannel Channel,
    SymbolKind Kind,
    string CanonicalKey,
    bool IsBestEffort)
{
    public static SymbolIdentity Create(
        CodebaseKind codebase,
        CodeChannel channel,
        SymbolKind kind,
        string qualifiedName,
        bool isBestEffort = false)
    {
        if (codebase == CodebaseKind.ScheduleI && channel != CodeChannel.Installed)
            throw new ArgumentException("Schedule I only supports the Installed channel.", nameof(channel));

        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var canonicalKey = $"{codebase}:{channel}:{kind}:{CanonicalSignatureRenderer.RenderType(qualifiedName)}";
        return new SymbolIdentity(codebase, channel, kind, canonicalKey, isBestEffort);
    }

    public string Fingerprint()
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalKey))).ToLowerInvariant();
    }
}
