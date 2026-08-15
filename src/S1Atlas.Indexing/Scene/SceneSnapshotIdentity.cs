using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Indexing.Scene;

public sealed record SceneSnapshotContainerFact(
    string RelativePath,
    long Size,
    string Sha256,
    string SidecarManifest);

public static class SceneSnapshotIdentity
{
    private const int IdentityVersion = 1;

    public static string Create(
        string buildId,
        string validatedExtractionId,
        string inputManifestDigest,
        string codeIndexId,
        string parserId,
        string parserVersion,
        int serializedFileSchemaVersion,
        IReadOnlyList<SceneSnapshotContainerFact> containers)
    {
        RequireText(buildId, nameof(buildId));
        RequireText(validatedExtractionId, nameof(validatedExtractionId));
        RequireSha256(inputManifestDigest, nameof(inputManifestDigest));
        RequireText(codeIndexId, nameof(codeIndexId));
        RequireText(parserId, nameof(parserId));
        RequireText(parserVersion, nameof(parserVersion));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serializedFileSchemaVersion);
        ArgumentNullException.ThrowIfNull(containers);

        var ordered = containers.OrderBy(container => container.RelativePath, StringComparer.Ordinal).ToArray();
        if (ordered.Select(container => container.RelativePath).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Scene snapshot container paths must be unique.", nameof(containers));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "scene-snapshot");
        Append(hash, IdentityVersion);
        Append(hash, buildId);
        Append(hash, validatedExtractionId);
        Append(hash, inputManifestDigest);
        Append(hash, codeIndexId);
        Append(hash, parserId);
        Append(hash, parserVersion);
        Append(hash, serializedFileSchemaVersion);
        Append(hash, ordered.Length);

        foreach (var container in ordered)
        {
            RequireText(container.RelativePath, nameof(containers));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(container.Size, nameof(containers));
            RequireSha256(container.Sha256, nameof(containers));
            ArgumentNullException.ThrowIfNull(container.SidecarManifest);
            Append(hash, container.RelativePath.Replace('\\', '/'));
            Append(hash, container.Size);
            Append(hash, container.Sha256);
            Append(hash, container.SidecarManifest);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, int value) =>
        Append(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, long value) =>
        Append(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void RequireText(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

    private static void RequireSha256(string value, string parameterName)
    {
        RequireText(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The value must be a lower-case SHA-256 digest.", parameterName);
    }
}
