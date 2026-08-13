using S1Atlas.Core.Tools;
using Xunit;

namespace S1Atlas.Core.Tests.Tools;

public sealed class ToolInstanceIdTests
{
    [Fact]
    public void Create_SameBytesAtDifferentPaths_ReturnsSameId()
    {
        var first = ToolInstanceId.Create(
            "cpp2il",
            new string('a', 64),
            "win-x64",
            ToolTrustLevel.ManagedPinned);
        var second = ToolInstanceId.Create(
            "cpp2il",
            new string('a', 64),
            "win-x64",
            ToolTrustLevel.ManagedPinned);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Create_WhenTrustLevelChanges_ReturnsDifferentId()
    {
        var managed = ToolInstanceId.Create(
            "cpp2il",
            new string('a', 64),
            "win-x64",
            ToolTrustLevel.ManagedPinned);
        var custom = ToolInstanceId.Create(
            "cpp2il",
            new string('a', 64),
            "win-x64",
            ToolTrustLevel.CustomOverride);

        Assert.NotEqual(managed, custom);
    }

    [Fact]
    public void Create_WhenExecutableHashChanges_ReturnsDifferentId()
    {
        var first = ToolInstanceId.Create(
            "cpp2il",
            new string('a', 64),
            "win-x64",
            ToolTrustLevel.ManagedPinned);
        var second = ToolInstanceId.Create(
            "cpp2il",
            new string('b', 64),
            "win-x64",
            ToolTrustLevel.ManagedPinned);

        Assert.NotEqual(first, second);
    }
}
