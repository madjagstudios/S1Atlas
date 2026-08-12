using System.Security.Cryptography;
using S1Atlas.Core.Hashing;

namespace S1Atlas.Extraction.Hashing;

public sealed class Sha256FileHasher : IFileHasher
{
    public async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cannot hash a file that does not exist.", path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
