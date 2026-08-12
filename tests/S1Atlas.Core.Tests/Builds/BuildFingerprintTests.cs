using S1Atlas.Core.Builds;
using Xunit;

namespace S1Atlas.Core.Tests.Builds;

public sealed class BuildFingerprintTests
{
    [Fact]
    public void Create_WithSameHashes_ReturnsSameId()
    {
        var first = BuildFingerprint.Create("aaa", "bbb");
        var second = BuildFingerprint.Create("aaa", "bbb");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_WhenMetadataHashChanges_ReturnsDifferentId()
    {
        var first = BuildFingerprint.Create("aaa", "bbb");
        var second = BuildFingerprint.Create("aaa", "ccc");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Create_WhenGameAssemblyHashChanges_ReturnsDifferentId()
    {
        var first = BuildFingerprint.Create("aaa", "bbb");
        var second = BuildFingerprint.Create("ddd", "bbb");

        Assert.NotEqual(first, second);
    }
}
