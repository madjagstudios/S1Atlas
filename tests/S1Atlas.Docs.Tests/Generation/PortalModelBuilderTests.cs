using S1Atlas.Docs.Generation;
using Xunit;

namespace S1Atlas.Docs.Tests.Generation;

public sealed class PortalModelBuilderTests
{
    [Fact]
    public void Docs_generation_request_keeps_build_pin_and_output_directory_explicit()
    {
        var request = new DocsGenerationRequest("build-current", "portal");

        Assert.Equal("build-current", request.RequestedBuildId);
        Assert.Equal("portal", request.OutputDirectory);
    }
}
