using Xunit;
using S1Atlas.Indexing;

namespace S1Atlas.Indexing.Tests;

public sealed class BootstrapTests
{
    [Fact]
    public void IndexingAssemblyIsAvailable()
    {
        Assert.NotNull(typeof(IndexingAssemblyMarker));
    }
}
