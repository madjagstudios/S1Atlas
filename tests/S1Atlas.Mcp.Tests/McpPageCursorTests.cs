using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class McpPageCursorTests
{
    [Fact]
    public void RoundTrip_PreservesHashAndOffset()
    {
        var cursor = McpPageCursor.Encode("hash-a", 50);

        Assert.True(McpPageCursor.TryDecodeShape(cursor, out var hash, out var offset));
        Assert.Equal("hash-a", hash);
        Assert.Equal(50, offset);
    }

    [Fact]
    public void Encode_ProducesUrlSafeOpaqueText()
    {
        var cursor = McpPageCursor.Encode("hash-a", 0);

        Assert.NotEmpty(cursor);
        Assert.DoesNotContain("+", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("/", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("=", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-a", cursor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-cursor!!")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MalformedCursor_DoesNotDecode(string? cursor)
    {
        Assert.False(McpPageCursor.TryDecodeShape(cursor, out _, out _));
    }

    [Fact]
    public void TamperedCursor_DoesNotDecode()
    {
        var cursor = McpPageCursor.Encode("hash-a", 50);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "BB" : "AA");

        Assert.False(McpPageCursor.TryDecodeShape(tampered, out _, out _));
    }

    [Fact]
    public void HashFor_IsDeterministicAndSensitiveToArguments()
    {
        var first = McpPageCursor.HashFor("find_callers", ["a", "b"], "build-1", "index-1");
        var same = McpPageCursor.HashFor("find_callers", ["a", "b"], "build-1", "index-1");
        var otherArgs = McpPageCursor.HashFor("find_callers", ["a", "c"], "build-1", "index-1");
        var otherIndex = McpPageCursor.HashFor("find_callers", ["a", "b"], "build-1", "index-2");

        Assert.Equal(64, first.Length);
        Assert.Equal(first, same);
        Assert.NotEqual(first, otherArgs);
        Assert.NotEqual(first, otherIndex);
    }

    [Fact]
    public void MintNextCursor_AdvancesOffsetOnlyWhenMoreRemain()
    {
        Assert.Null(McpPageCursor.MintNextCursor(false, "hash-a", 50, 10));

        var next = McpPageCursor.MintNextCursor(true, "hash-a", 50, 10);
        Assert.True(McpPageCursor.TryDecodeShape(next, out var hash, out var offset));
        Assert.Equal("hash-a", hash);
        Assert.Equal(60, offset);
    }
}
