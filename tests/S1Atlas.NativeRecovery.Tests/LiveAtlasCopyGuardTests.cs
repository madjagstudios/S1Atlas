using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

/// <summary>
/// Guards the AT-62 invariant without needing the game: <see cref="LiveAtlasCopy"/>
/// refuses any data root outside the test temp area, and copies a (fake) live atlas
/// into temp for the test to use.
/// </summary>
public sealed class LiveAtlasCopyGuardTests
{
    [Fact]
    public void RequireTestTempRoot_AcceptsDirectoriesStrictlyInsideTemp()
    {
        var inside = Path.Combine(
            Path.GetTempPath(), "s1atlas-copy-guard-" + Guid.NewGuid().ToString("N"));

        LiveAtlasCopy.RequireTestTempRoot(inside);
    }

    [Fact]
    public void RequireTestTempRoot_RejectsTheTempDirectoryItself()
    {
        Assert.Throws<InvalidOperationException>(
            () => LiveAtlasCopy.RequireTestTempRoot(Path.GetTempPath()));
    }

    [Fact]
    public void RequireTestTempRoot_RejectsRootsOutsideTemp()
    {
        var outside = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "..", "s1atlas-copy-guard-" + Guid.NewGuid().ToString("N")));

        Assert.Throws<InvalidOperationException>(
            () => LiveAtlasCopy.RequireTestTempRoot(outside));
    }

    [Fact]
    public async Task CreateAsync_CopiesAtlasContentsIntoTempAndRemovesThemOnDispose()
    {
        var liveRoot = Path.Combine(
            Path.GetTempPath(), "s1atlas-fake-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(liveRoot, "builds", "some-build"));
        await File.WriteAllTextAsync(
            Path.Combine(liveRoot, "atlas.db"), "fake-database", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(liveRoot, "builds", "some-build", "index.json"), "{}",
            TestContext.Current.CancellationToken);

        try
        {
            await using var copy = await LiveAtlasCopy.CreateAsync(
                liveRoot, TestContext.Current.CancellationToken);
            Assert.NotNull(copy);
            Assert.NotEqual(
                Path.GetFullPath(liveRoot).TrimEnd(Path.DirectorySeparatorChar),
                copy!.DataRoot.TrimEnd(Path.DirectorySeparatorChar));
            LiveAtlasCopy.RequireTestTempRoot(copy.DataRoot);
            Assert.Equal(
                "fake-database",
                await File.ReadAllTextAsync(
                    Path.Combine(copy.DataRoot, "atlas.db"),
                    TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(
                copy.DataRoot, "builds", "some-build", "index.json")));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(liveRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_WithoutDatabase_ThrowsFileNotFound()
    {
        var emptyRoot = Path.Combine(
            Path.GetTempPath(), "s1atlas-fake-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => LiveAtlasCopy.CreateAsync(emptyRoot, TestContext.Current.CancellationToken));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(emptyRoot);
        }
    }
}
