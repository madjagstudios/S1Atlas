using S1Atlas.Extraction.Hashing;
using Xunit;

namespace S1Atlas.Extraction.Tests.Hashing;

public sealed class Sha256FileHasherTests
{
    [Fact]
    public async Task ComputeSha256Async_ForEmptyFile_ReturnsKnownHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [], cancellationToken);
            var hasher = new Sha256FileHasher();

            var result = await hasher.ComputeSha256Async(path, cancellationToken);

            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ComputeSha256Async_WhenFileDoesNotExist_Throws()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var hasher = new Sha256FileHasher();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => hasher.ComputeSha256Async(path, cancellationToken));
    }
}
