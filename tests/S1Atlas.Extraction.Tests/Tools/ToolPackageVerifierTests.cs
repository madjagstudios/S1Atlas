using System.Security.Cryptography;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolPackageVerifierTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public async Task VerifyAsync_WhenSizeAndShaMatch_ReturnsObservedFacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[] { 9, 8, 7, 6 };
        var path = await WritePackageAsync(bytes, cancellationToken);
        var package = CreatePackage(bytes.Length, Hash(bytes));
        var verifier = new ToolPackageVerifier(new Sha256FileHasher());

        var verified = await verifier.VerifyAsync(
            path,
            package,
            cancellationToken);

        Assert.Equal(Path.GetFullPath(path), verified.Path);
        Assert.Equal(bytes.Length, verified.Size);
        Assert.Equal(Hash(bytes), verified.Sha256);
    }

    [Fact]
    public async Task VerifyAsync_WhenSizeDiffers_ThrowsToolSizeMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[] { 1, 2, 3 };
        var path = await WritePackageAsync(bytes, cancellationToken);
        var package = CreatePackage(bytes.Length + 1, Hash(bytes));
        var verifier = new ToolPackageVerifier(new Sha256FileHasher());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            verifier.VerifyAsync(path, package, cancellationToken));

        Assert.Equal("ToolSizeMismatch", exception.Code);
    }

    [Fact]
    public async Task VerifyAsync_WhenShaDiffers_ThrowsToolChecksumMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[] { 1, 2, 3 };
        var path = await WritePackageAsync(bytes, cancellationToken);
        var package = CreatePackage(bytes.Length, new string('f', 64));
        var verifier = new ToolPackageVerifier(new Sha256FileHasher());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            verifier.VerifyAsync(path, package, cancellationToken));

        Assert.Equal("ToolChecksumMismatch", exception.Code);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<string> WritePackageAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_temporaryDirectory, "package.bin");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private static ToolPackageDefinition CreatePackage(long size, string sha256) =>
        new(
            ToolPackageKind.SingleFile,
            ArchiveFormat: null,
            SourceUri: new Uri("https://example.test/tool.bin"),
            ReleaseUri: new Uri("https://example.test/releases/tool"),
            AssetName: "tool.bin",
            ExpectedSize: size,
            Sha256: sha256,
            ExecutableRelativePath: "Cpp2IL.exe",
            Limits: new ToolSafetyLimits(size, size, 1));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
