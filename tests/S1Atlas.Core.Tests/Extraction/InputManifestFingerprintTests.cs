using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class InputManifestFingerprintTests
{
    [Fact]
    public void Create_WhenEntriesAreReorderedOrTimestampsChange_ReturnsSameDigest()
    {
        var earlier = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var later = earlier.AddDays(1);
        var first = new InputManifest([
            new InputManifestEntry("GameAssembly.dll", "gameAssembly", 10, new string('a', 64), earlier),
            new InputManifestEntry("Data\\Metadata\\global-metadata.dat", "metadata", 20, new string('b', 64), earlier)
        ]);
        var second = new InputManifest([
            new InputManifestEntry("Data/Metadata/global-metadata.dat", "metadata", 20, new string('b', 64), later),
            new InputManifestEntry("GameAssembly.dll", "gameAssembly", 10, new string('a', 64), later)
        ]);

        Assert.Equal(
            InputManifestFingerprint.Create(first),
            InputManifestFingerprint.Create(second));
    }

    public static IEnumerable<object[]> IdentityMutations()
    {
        yield return [new InputManifest([new InputManifestEntry("GameAssembly.dll", "gameAssembly", 11, new string('a', 64), DateTimeOffset.UtcNow)])];
        yield return [new InputManifest([new InputManifestEntry("GameAssembly.dll", "metadata", 10, new string('a', 64), DateTimeOffset.UtcNow)])];
        yield return [new InputManifest([new InputManifestEntry("GameAssembly.dll", "gameAssembly", 10, new string('b', 64), DateTimeOffset.UtcNow)])];
    }

    [Theory]
    [MemberData(nameof(IdentityMutations))]
    public void Create_WhenRoleSizeOrHashChanges_ChangesDigest(InputManifest changed)
    {
        var baseline = new InputManifest([
            new InputManifestEntry("GameAssembly.dll", "gameAssembly", 10, new string('a', 64), DateTimeOffset.UtcNow)
        ]);

        Assert.NotEqual(
            InputManifestFingerprint.Create(baseline),
            InputManifestFingerprint.Create(changed));
    }

    [Theory]
    [InlineData("/rooted.dll")]
    [InlineData("C:\\rooted.dll")]
    [InlineData("Data/../global-metadata.dat")]
    [InlineData("Data//global-metadata.dat")]
    public void Create_WhenRelativePathIsUnsafe_RejectsManifest(string relativePath)
    {
        var manifest = new InputManifest([
            new InputManifestEntry(relativePath, "metadata", 10, new string('a', 64), DateTimeOffset.UtcNow)
        ]);

        Assert.Throws<ArgumentException>(() => InputManifestFingerprint.Create(manifest));
    }
}
