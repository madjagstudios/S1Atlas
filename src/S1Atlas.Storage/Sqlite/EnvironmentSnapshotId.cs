using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Environment;

namespace S1Atlas.Storage.Sqlite;

internal static class EnvironmentSnapshotId
{
    public static string Create(EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, snapshot.Build.BuildId);
        Append(hash, snapshot.AtlasVersion);

        var dependencies = snapshot.Dependencies
            .OrderBy(dependency => dependency.Kind)
            .ThenBy(dependency => dependency.Version ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Append(hash, dependencies.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var dependency in dependencies)
        {
            Append(hash, dependency.Kind.ToString());
            Append(hash, dependency.IsInstalled ? "1" : "0");
            Append(hash, dependency.Version ?? string.Empty);
            Append(hash, NormalizePath(dependency.Path));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path
            .GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }
}
