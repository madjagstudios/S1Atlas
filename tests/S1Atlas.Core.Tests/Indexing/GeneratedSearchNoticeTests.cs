using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class GeneratedSearchNoticeTests
{
    [Fact]
    public void ForHidden_ZeroReturnsNull() =>
        Assert.Null(GeneratedSearchNotice.ForHidden(0));

    [Fact]
    public void ForHidden_RendersExactServiceWording() =>
        Assert.Equal(
            "2 generated result(s) hidden. Re-run with includeGenerated to include them.",
            GeneratedSearchNotice.ForHidden(2));

    [Fact]
    public void TryParse_RoundTripsRenderedNotice()
    {
        Assert.True(GeneratedSearchNotice.TryParseHiddenCount(GeneratedSearchNotice.ForHidden(3), out var hidden));
        Assert.Equal(3, hidden);
    }

    [Fact]
    public void TryParse_RejectsNullAndForeignNotices()
    {
        Assert.False(GeneratedSearchNotice.TryParseHiddenCount(null, out _));
        Assert.False(
            GeneratedSearchNotice.TryParseHiddenCount(
                "search index not built; run any s1atlas write command, e.g. `s1atlas index`, to upgrade",
                out _));
    }

    [Fact]
    public async Task ForHiddenAsync_RendersUnfilteredMinusShown()
    {
        var notice = await GeneratedSearchNotice.ForHiddenAsync(() => Task.FromResult(5), 3, includeGenerated: false);

        Assert.Equal(
            "2 generated result(s) hidden. Re-run with includeGenerated to include them.",
            notice);
    }

    [Fact]
    public async Task ForHiddenAsync_SkipsSecondCountWhenIncluded()
    {
        var called = false;
        var notice = await GeneratedSearchNotice.ForHiddenAsync(
            () =>
            {
                called = true;
                return Task.FromResult(5);
            },
            5,
            includeGenerated: true);

        Assert.Null(notice);
        Assert.False(called);
    }
}
