using S1Atlas.Core.Identity;
using Xunit;

namespace S1Atlas.Core.Tests.Identity;

public sealed class CanonicalHashWriterTests
{
    [Fact]
    public void Complete_WithSameTypedValues_ReturnsSameLowercaseSha256()
    {
        using var firstWriter = new CanonicalHashWriter("test-identity", 1);
        firstWriter.AppendString("alpha");
        firstWriter.AppendNullableString(null);
        firstWriter.AppendInt32(42);
        firstWriter.AppendInt64(9_876_543_210L);
        firstWriter.AppendBoolean(true);

        using var secondWriter = new CanonicalHashWriter("test-identity", 1);
        secondWriter.AppendString("alpha");
        secondWriter.AppendNullableString(null);
        secondWriter.AppendInt32(42);
        secondWriter.AppendInt64(9_876_543_210L);
        secondWriter.AppendBoolean(true);

        var first = firstWriter.Complete();
        var second = secondWriter.Complete();

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Complete_NullAndEmptyString_ReturnDifferentDigests()
    {
        using var nullWriter = new CanonicalHashWriter("test-identity", 1);
        nullWriter.AppendNullableString(null);

        using var emptyWriter = new CanonicalHashWriter("test-identity", 1);
        emptyWriter.AppendNullableString(string.Empty);

        Assert.NotEqual(nullWriter.Complete(), emptyWriter.Complete());
    }

    [Fact]
    public void Complete_ChangingIdentityKindOrVersion_ChangesDigest()
    {
        using var baselineWriter = new CanonicalHashWriter("tool-definition", 1);
        baselineWriter.AppendString("same-value");

        using var changedKindWriter = new CanonicalHashWriter("tool-instance", 1);
        changedKindWriter.AppendString("same-value");

        using var changedVersionWriter = new CanonicalHashWriter("tool-definition", 2);
        changedVersionWriter.AppendString("same-value");

        var baseline = baselineWriter.Complete();

        Assert.NotEqual(baseline, changedKindWriter.Complete());
        Assert.NotEqual(baseline, changedVersionWriter.Complete());
    }
}
