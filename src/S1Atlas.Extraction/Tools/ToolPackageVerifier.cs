using S1Atlas.Core.Hashing;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed record VerifiedToolPackage(
    string Path,
    long Size,
    string Sha256);

internal sealed class ToolPackageVerifier
{
    private readonly IFileHasher _fileHasher;

    public ToolPackageVerifier(IFileHasher fileHasher)
    {
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
    }

    public async Task<VerifiedToolPackage> VerifyAsync(
        string packagePath,
        ToolPackageDefinition package,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(package);

        var fullPath = Path.GetFullPath(packagePath);
        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "The downloaded tool package does not exist.",
                    fullPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            throw new ToolOperationException(
                "ToolDownloadFailed",
                $"The downloaded tool package could not be inspected: {exception.Message}",
                exception);
        }

        if (file.Length != package.ExpectedSize)
        {
            throw new ToolOperationException(
                "ToolSizeMismatch",
                $"The tool package size is {file.Length} bytes; " +
                $"the committed definition requires {package.ExpectedSize} bytes.");
        }

        string observedSha256;
        try
        {
            observedSha256 = await _fileHasher.ComputeSha256Async(
                fullPath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ToolOperationException(
                "ToolDownloadFailed",
                $"The downloaded tool package could not be hashed: {exception.Message}",
                exception);
        }

        if (!string.Equals(
                observedSha256,
                package.Sha256,
                StringComparison.Ordinal))
        {
            throw new ToolOperationException(
                "ToolChecksumMismatch",
                "The downloaded tool package SHA-256 does not match the " +
                "repository-controlled definition.");
        }

        return new VerifiedToolPackage(
            fullPath,
            file.Length,
            observedSha256);
    }
}
