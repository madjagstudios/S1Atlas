using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Core.Builds;

public static class BuildFingerprint
{
    public static string Create(string gameAssemblySha256, string metadataSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAssemblySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataSha256);

        var fingerprintInput = $"{gameAssemblySha256}:{metadataSha256}";
        var bytes = Encoding.UTF8.GetBytes(fingerprintInput);

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
