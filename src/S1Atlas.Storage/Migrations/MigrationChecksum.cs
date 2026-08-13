using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Storage.Migrations;

internal static class MigrationChecksum
{
    public static string Compute(int version, string name, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var canonical = $"{version}\n{name}\n{sql.Replace("\r\n", "\n", StringComparison.Ordinal)}";
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
