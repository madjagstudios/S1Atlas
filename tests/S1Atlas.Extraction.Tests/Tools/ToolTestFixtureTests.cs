using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolTestFixtureTests
{
    [Fact]
    public async Task DeleteTemporaryDirectoryAsync_RetriesPastTransientLock_AndDeletes()
    {
        var directory = ToolTestFixture.CreateTemporaryDirectory();
        var file = Path.Combine(directory, "locked.bin");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);

        // Hold an exclusive lock on a file inside the directory, mirroring the
        // transient image-lock a just-killed process holds on its executable,
        // then release it shortly after deletion begins.
        var token = TestContext.Current.CancellationToken;
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200, token);
            stream.Dispose();
        }, token);

        await ToolTestFixture.DeleteTemporaryDirectoryAsync(directory);
        await release;

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task DeleteTemporaryDirectoryAsync_MissingDirectory_IsNoOp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"s1atlas-missing-{Guid.NewGuid():N}");

        await ToolTestFixture.DeleteTemporaryDirectoryAsync(directory);

        Assert.False(Directory.Exists(directory));
    }
}
