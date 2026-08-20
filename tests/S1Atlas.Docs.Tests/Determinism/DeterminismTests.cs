using S1Atlas.Docs.Determinism;
using Xunit;

namespace S1Atlas.Docs.Tests.Determinism;

public sealed class DeterminismTests
{
    [Fact]
    public void Text_and_search_assets_are_stable_and_lf_normalized()
    {
        var text = new DeterministicText();

        Assert.Equal("two", text.FormatCount(2));
        Assert.Equal("showing two of 12", text.FormatCoverage(2, 12));
        Assert.Equal("one method", text.FormatPlural(1, "method", "methods"));
        Assert.Equal("two methods", text.FormatPlural(2, "method", "methods"));
        Assert.Equal("a\nb\n", text.NormalizeLf("a\r\nb\r"));
    }
}
