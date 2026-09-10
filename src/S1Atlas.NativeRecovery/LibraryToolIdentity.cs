using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace S1Atlas.NativeRecovery;

public sealed record LibraryPin(string PackageId, string Version, string ContentSha256);

public static class LibraryToolIdentity
{
    public const string ToolName = "s1atlas-native-recovery-libs";

    public static string ComputeToolSha256(IReadOnlyList<LibraryPin> pins)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "s1atlas-native-recovery-libs-v1");
        foreach (var pin in pins.OrderBy(pin => pin.PackageId, StringComparer.Ordinal))
        {
            Append(hash, pin.PackageId);
            Append(hash, pin.Version);
            Append(hash, pin.ContentSha256);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ToolVersion(IReadOnlyList<LibraryPin> pins) =>
        string.Join(';', pins.Select(pin => $"{ShortName(pin.PackageId)}={pin.Version}"));

    private static string ShortName(string packageId)
    {
        var lastDot = packageId.LastIndexOf('.');
        var name = lastDot >= 0 ? packageId[(lastDot + 1)..] : packageId;
        return name.ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
