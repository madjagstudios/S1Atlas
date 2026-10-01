using S1Atlas.Core.Display;
using Xunit;

namespace S1Atlas.Core.Tests.Display;

public sealed class PortalPathDisplayTests
{
    [Theory]
    [InlineData(@"C:\game", @"C:\game\GameAssembly.dll", "GameAssembly.dll")]
    [InlineData(@"C:\game", @"C:\game\MelonLoader\net6\MelonLoader.dll", "MelonLoader/net6/MelonLoader.dll")]
    [InlineData(@"C:\game\", @"C:\game\Sub\F.dll", "Sub/F.dll")]
    [InlineData("C:/game", @"C:\game\Sub\F.dll", "Sub/F.dll")]
    [InlineData(@"C:\Game", @"c:\GAME\Sub\F.dll", "Sub/F.dll")]
    [InlineData("/opt/game", "/opt/game/sub/f.so", "sub/f.so")]
    [InlineData("/opt/game/", "/opt/game/sub/f.so", "sub/f.so")]
    [InlineData(@"\\server\share\game", @"\\server\share\game\bin\x.dll", "bin/x.dll")]
    [InlineData(@"\\SERVER\SHARE\game", @"\\server\share\GAME\bin\x.dll", "bin/x.dll")]
    [InlineData(@"C:\game", @"C:\game\ \F.dll", "F.dll")]
    public void InsideRoot_renders_relative_with_forward_slashes(string root, string path, string expected)
    {
        Assert.Equal(expected, PortalPathDisplay.ToDisplayPath(path, root));
    }

    [Theory]
    [InlineData(@"C:\game", @"D:\other\Tool.dll", "Tool.dll (outside the installation root)")]
    [InlineData(@"C:\game", @"C:\other\Tool.dll", "Tool.dll (outside the installation root)")]
    [InlineData(@"C:\game", @"C:\gameSibling\Tool.dll", "Tool.dll (outside the installation root)")]
    [InlineData("/opt/game", "/opt/other/f.so", "f.so (outside the installation root)")]
    [InlineData("/opt/Game", "/opt/game/f.so", "f.so (outside the installation root)")]
    [InlineData(@"C:\game", "/opt/game/f.so", "f.so (outside the installation root)")]
    [InlineData(@"\\server\share\game", @"\\other\share\game\f.dll", "f.dll (outside the installation root)")]
    [InlineData(@"\\server\share\game", @"C:\game\f.dll", "f.dll (outside the installation root)")]
    [InlineData(@"C:\game", @"C:\game", "game (outside the installation root)")]
    public void OutsideRoot_renders_file_name_with_marker(string root, string path, string expected)
    {
        Assert.Equal(expected, PortalPathDisplay.ToDisplayPath(path, root));
    }

    [Theory]
    [InlineData(@"C:\game\..\evil\f.dll", @"C:\game", "(outside the installation root)")]
    [InlineData(@"..\rel\f.dll", @"C:\game", "(outside the installation root)")]
    [InlineData(@"C:\game\sub\..\f.dll", @"C:\game", "(outside the installation root)")]
    public void DotSegments_never_reach_output(string path, string? root, string expected)
    {
        Assert.Equal(expected, PortalPathDisplay.ToDisplayPath(path, root));
    }

    [Theory]
    [InlineData("tools/s1api", @"C:\game", "tools/s1api")]
    [InlineData(@"tools\s1api", @"C:\game", "tools/s1api")]
    [InlineData("tools/s1api", null, "tools/s1api")]
    public void RelativePath_renders_as_is(string path, string? root, string expected)
    {
        Assert.Equal(expected, PortalPathDisplay.ToDisplayPath(path, root));
    }

    [Theory]
    [InlineData(null, @"C:\game")]
    [InlineData("", @"C:\game")]
    [InlineData("   ", @"C:\game")]
    public void NullOrEmptyPath_renders_nothing(string? path, string? root)
    {
        Assert.Null(PortalPathDisplay.ToDisplayPath(path, root));
    }

    [Theory]
    [InlineData(@"C:\game\Sub\F.dll", null, "F.dll (outside the installation root)")]
    [InlineData(@"C:\game\Sub\F.dll", "", "F.dll (outside the installation root)")]
    [InlineData("/opt/game/f.so", null, "f.so (outside the installation root)")]
    public void MissingRoot_renders_file_name_with_marker(string path, string? root, string expected)
    {
        Assert.Equal(expected, PortalPathDisplay.ToDisplayPath(path, root));
    }
}
