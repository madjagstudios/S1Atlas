using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Environment;

namespace S1Atlas.Storage.Sqlite;

internal static class EnvironmentSnapshotId
{
    public static string Create(EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IdentityVersion != 2)
        {
            throw new InvalidOperationException(
                "Only identity-version 2 snapshots can receive a new snapshot ID.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "environment-snapshot");
        Append(hash, snapshot.IdentityVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, snapshot.Build.BuildId);
        Append(hash, snapshot.AtlasVersion);
        Append(hash, snapshot.Installation.ExecutableVersion ?? string.Empty);
        Append(hash, snapshot.Installation.SteamAppId ?? string.Empty);
        Append(hash, snapshot.Installation.SteamBuildId ?? string.Empty);
        Append(hash, NormalizePath(snapshot.Installation.InstallationRoot));
        Append(hash, NormalizePath(snapshot.Installation.GameAssemblyPath));
        Append(hash, NormalizePath(snapshot.Installation.GlobalMetadataPath));

        var dependencies = OrderDependencies(snapshot.Dependencies);
        Append(hash, dependencies.Length.ToString(CultureInfo.InvariantCulture));

        foreach (var dependency in dependencies)
        {
            Append(hash, dependency.Kind.ToString());
            Append(hash, dependency.IsInstalled ? "1" : "0");
            Append(hash, dependency.Version ?? string.Empty);
            Append(hash, NormalizePath(dependency.Path));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static DependencyVersion[] OrderDependencies(
        IEnumerable<DependencyVersion> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        return dependencies
            .OrderBy(dependency => dependency.Kind)
            .ThenBy(
                dependency => dependency.Version ?? string.Empty,
                StringComparer.Ordinal)
            .ThenBy(
                dependency => dependency.Path ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                dependency => dependency.Path ?? string.Empty,
                StringComparer.Ordinal)
            .ToArray();
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
