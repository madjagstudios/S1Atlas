using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ArtifactManifestFingerprintTests
{
    [Fact]
    public void Create_WithSamePathSizeAndHash_ReturnsSameDigest()
    {
        var first = new ArtifactManifest(1, [Entry("reconstructed/Assembly-CSharp.dll", 100, new string('a', 64))]);
        var second = new ArtifactManifest(1, [Entry("reconstructed/Assembly-CSharp.dll", 100, new string('a', 64))]);

        Assert.Equal(
            ArtifactManifestFingerprint.Create(first),
            ArtifactManifestFingerprint.Create(second));
    }

    [Fact]
    public void Create_WhenEntryOrderChanges_ReturnsSameDigest()
    {
        var first = new ArtifactManifest(1, [
            Entry("reconstructed/a.dll", 10, new string('a', 64)),
            Entry("reconstructed/b.dll", 20, new string('b', 64))
        ]);
        var second = new ArtifactManifest(1, [
            Entry("reconstructed/b.dll", 20, new string('b', 64)),
            Entry("reconstructed/a.dll", 10, new string('a', 64))
        ]);

        Assert.Equal(
            ArtifactManifestFingerprint.Create(first),
            ArtifactManifestFingerprint.Create(second));
    }

    public static IEnumerable<object[]> ManifestMutations()
    {
        yield return [new ArtifactManifest(1, [Entry("reconstructed/other.dll", 10, new string('a', 64))])];
        yield return [new ArtifactManifest(1, [Entry("reconstructed/a.dll", 11, new string('a', 64))])];
        yield return [new ArtifactManifest(1, [Entry("reconstructed/a.dll", 10, new string('b', 64))])];
    }

    [Theory]
    [MemberData(nameof(ManifestMutations))]
    public void Create_WhenPathSizeOrHashChanges_ReturnsDifferentDigest(ArtifactManifest changed)
    {
        var baseline = new ArtifactManifest(1, [Entry("reconstructed/a.dll", 10, new string('a', 64))]);

        Assert.NotEqual(
            ArtifactManifestFingerprint.Create(baseline),
            ArtifactManifestFingerprint.Create(changed));
    }

    [Fact]
    public void Create_WhenOnlyClassificationOrMetadataCountsChange_ReturnsSameDigest()
    {
        var baseline = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/a.dll", ArtifactKind.ManagedAssembly, 10, new string('a', 64),
                "A", "A.dll", 1, 2, 3, 4, 5)
        ]);
        var changed = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/a.dll", ArtifactKind.NativeLibrary, 10, new string('a', 64),
                null, null, null, null, null, null, null)
        ]);

        Assert.Equal(
            ArtifactManifestFingerprint.Create(baseline),
            ArtifactManifestFingerprint.Create(changed));
    }

    [Fact]
    public void Create_WhenPathsDifferOnlyByWindowsCase_RejectsAmbiguousManifest()
    {
        var manifest = new ArtifactManifest(1, [
            Entry("reconstructed/Assembly.dll", 10, new string('a', 64)),
            Entry("reconstructed/assembly.dll", 10, new string('a', 64))
        ]);

        Assert.Throws<ArgumentException>(() => ArtifactManifestFingerprint.Create(manifest));
    }

    private static ArtifactManifestEntry Entry(string relativePath, long size, string sha256) => new(
        relativePath, ArtifactKind.Other, size, sha256, null, null, null, null, null, null, null);
}
