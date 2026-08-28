using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Indexing.ReferenceMods;

public sealed class ReferenceModInputHasher
{
    private readonly Func<string, byte[]>? _hashOverride;

    public ReferenceModInputHasher(Func<string, byte[]>? hashOverride = null)
    {
        _hashOverride = hashOverride;
    }

    public async Task<ReferenceModHashResult> HashAsync(
        IReadOnlyList<ReferenceModInputFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();

        var sorted = files
            .OrderBy(file => file.ModId, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var results = new List<ReferenceModInputFileHash>(sorted.Length);
        foreach (var file in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveRoot(file);
            if (!ReferenceModPathSafety.TryObserveRegularFile(root, file.FullPath, out var before))
            {
                throw new InvalidDataException($"Reference mod input '{file.RelativePath}' is missing or unsafe.");
            }

            var hash = _hashOverride is null
                ? await ComputeHashAsync(file.FullPath, cancellationToken)
                : Convert.ToHexString(_hashOverride(file.FullPath)).ToLowerInvariant();

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceModPathSafety.TryObserveRegularFile(root, file.FullPath, out var after) ||
                !before.IsStableWith(after))
            {
                throw new InvalidDataException($"Reference mod input '{file.RelativePath}' drifted while hashing.");
            }

            results.Add(new ReferenceModInputFileHash(
                file.ModId,
                file.FullPath,
                file.RelativePath,
                file.Kind,
                file.DeclaredDocumentKind,
                file.DisplayName,
                file.Version,
                file.License,
                hash,
                after.Length));
        }

        return new ReferenceModHashResult(
            results,
            ComputeCollectionContentHash(results));
    }

    private static string ResolveRoot(ReferenceModInputFile file)
    {
        var current = Path.GetDirectoryName(file.FullPath)!;
        var segments = file.RelativePath.Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            current = Path.GetFullPath(Path.Combine(current, ".."));
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ComputeCollectionContentHash(IReadOnlyList<ReferenceModInputFileHash> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "reference-mod-collection");
        Append(hash, "1");
        Append(hash, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var file in files)
        {
            Append(hash, file.ModId);
            Append(hash, file.RelativePath);
            Append(hash, file.Kind.ToString());
            Append(hash, file.DeclaredDocumentKind ?? string.Empty);
            Append(hash, file.DisplayName);
            Append(hash, file.Version);
            Append(hash, file.License ?? string.Empty);
            Append(hash, file.Sha256);
            Append(hash, file.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}

public sealed record ReferenceModInputFileHash(
    string ModId,
    string FullPath,
    string RelativePath,
    ReferenceModInputKind Kind,
    string? DeclaredDocumentKind,
    string DisplayName,
    string Version,
    string? License,
    string Sha256,
    long ByteCount);

public sealed record ReferenceModHashResult(
    IReadOnlyList<ReferenceModInputFileHash> Files,
    string CollectionContentSha256);
