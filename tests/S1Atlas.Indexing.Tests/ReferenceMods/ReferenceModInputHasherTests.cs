using System.Text;
using S1Atlas.Indexing.ReferenceMods;
using Xunit;

namespace S1Atlas.Indexing.Tests.ReferenceMods;

public sealed class ReferenceModInputHasherTests
{
    [Fact]
    public void EncodeFrame_uses_little_endian_length_prefix()
    {
        var framed = ReferenceModInputHasher.EncodeFrame("hello");

        Assert.Equal(
            [0x05, 0x00, 0x00, 0x00, .. Encoding.UTF8.GetBytes("hello")],
            framed);
    }
}
