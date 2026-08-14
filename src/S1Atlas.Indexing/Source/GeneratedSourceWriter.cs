using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Source;

public sealed class GeneratedSourceWriter
{
    public async Task<IndexSourceFileRecord> WriteAsync(
        string stagingRoot,
        string relativePath,
        string sourceText,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(sourceText);
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new ArgumentException("Source paths must be relative and contained.", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated source escaped its staging root.");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var bytes = Encoding.UTF8.GetBytes(sourceText);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new IndexSourceFileRecord(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotId + "\n" + relativePath))).ToLowerInvariant(),
            snapshotId,
            relativePath.Replace('\\', '/'),
            hash,
            bytes.LongLength);
    }
}
