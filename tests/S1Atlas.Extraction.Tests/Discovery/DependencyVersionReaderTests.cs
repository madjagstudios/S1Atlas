using System.Security;
using S1Atlas.Extraction.Discovery;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class DependencyVersionReaderTests
{
    [Fact]
    public void TryReadVersion_WhenFileDisappears_ReturnsUnknownVersion()
    {
        var assemblyProbeCalled = false;
        var reader = new DependencyVersionReader(
            _ => throw new FileNotFoundException("simulated file race"),
            _ =>
            {
                assemblyProbeCalled = true;
                return "1.0.0";
            });

        var result = reader.TryReadVersion("missing.dll");

        Assert.Null(result);
        Assert.False(assemblyProbeCalled);
    }

    [Fact]
    public void TryReadVersion_WhenAssemblyProbeIsDenied_ReturnsUnknownVersion()
    {
        var reader = new DependencyVersionReader(
            _ => null,
            _ => throw new SecurityException("simulated denied version probe"));

        var result = reader.TryReadVersion("protected.dll");

        Assert.Null(result);
    }
}
