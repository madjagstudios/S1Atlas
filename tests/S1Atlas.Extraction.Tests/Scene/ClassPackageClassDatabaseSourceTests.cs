using System.Security.Cryptography;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

// The production class-package source is exercised without any Unity dump: a missing file is
// "not installed", a hash mismatch is refused before any byte is interpreted, and a file that
// matches its pin but is not a class package is reported as such. Decoding a real package is
// covered by the end-to-end smoke on an installed build, not by a committed binary.
public sealed class ClassPackageClassDatabaseSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-classdb-source-" + Guid.NewGuid().ToString("N"));

    public ClassPackageClassDatabaseSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Resolve_WhenPackageIsMissing_ReturnsNullAndReportsNotInstalled()
    {
        var source = new AssetsToolsUnitySerializedFileParser.ClassPackageSource(Path.Combine(_root, "classdata.tpk"), Descriptor(new string('a', 64)));

        Assert.False(source.IsInstalled);
        Assert.Null(((AssetsToolsUnitySerializedFileParser.IClassDatabaseSource)source).Resolve("2022.3.62f2"));
    }

    [Fact]
    public void Resolve_WhenPackageHashDoesNotMatchThePin_RefusesWithTheRepairHint()
    {
        var path = Path.Combine(_root, "classdata.tpk");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var source = new AssetsToolsUnitySerializedFileParser.ClassPackageSource(path, Descriptor(new string('a', 64)));

        Assert.True(source.IsInstalled);
        var failure = Assert.Throws<InvalidDataException>(() => ((AssetsToolsUnitySerializedFileParser.IClassDatabaseSource)source).Resolve("2022.3.62f2"));
        Assert.Contains("does not match its pinned SHA-256", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tools install unity-classdata --repair", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WhenPinnedFileIsNotAClassPackage_ReportsUnreadablePackage()
    {
        var path = Path.Combine(_root, "classdata.tpk");
        var bytes = new byte[64];
        File.WriteAllBytes(path, bytes);
        var source = new AssetsToolsUnitySerializedFileParser.ClassPackageSource(path, Descriptor(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));

        var failure = Assert.Throws<InvalidDataException>(() => ((AssetsToolsUnitySerializedFileParser.IClassDatabaseSource)source).Resolve("2022.3.62f2"));
        Assert.Contains("not a readable class package", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_RequiresALowerCaseSha256()
    {
        Assert.Throws<ArgumentException>(() => new UnityClassDatabaseDescriptor("unity-classdata", "v", new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new UnityClassDatabaseDescriptor("unity-classdata", "v", "abc"));
        Assert.Throws<ArgumentException>(() => new UnityClassDatabaseDescriptor("", "v", new string('a', 64)));
    }

    private static UnityClassDatabaseDescriptor Descriptor(string sha256) => new("unity-classdata", "uabea-test", sha256);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
