using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Mapping;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class EnvelopeHintTests
{
    [Fact]
    public void SymbolResolution_NoCompletedIndex_NamesIndex()
    {
        var authority = new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            null,
            "build-1",
            "extraction-1",
            "index-1",
            null,
            null,
            null);

        var envelope = EnvelopeMapper.FromSymbolResolution(
            authority,
            new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
            new HashSet<SymbolKind>());

        Assert.NotNull(envelope.Error);
        Assert.Equal("NoCompletedIndex", envelope.Error.Code);
        Assert.Equal(ReadinessFixCommands.Index, envelope.Error.Hint);
    }

    [Fact]
    public void ApiSelection_MissingInstalledIndex_NamesInstalledApiIndex()
    {
        var envelope = EnvelopeMapper.FromApiSelectionFailure<object>(
            new ApiIndexCatalogResult([], null, "build-1"),
            new ApiIndexSelection(
                CodebaseKind.S1Api,
                CodeChannel.Installed,
                ApiIndexAvailability.Unavailable,
                null,
                null,
                null,
                null,
                "No completed S1Api Installed API index is available."));

        Assert.NotNull(envelope.Error);
        Assert.Equal("NoCompletedIndex", envelope.Error.Code);
        Assert.Equal(
            ReadinessFixCommands.IndexApiInstalled(CodebaseKind.S1Api),
            envelope.Error.Hint);
    }

    [Fact]
    public void ApiSelection_MissingReleaseIndex_OmitsHint()
    {
        var envelope = EnvelopeMapper.FromApiSelectionFailure<object>(
            new ApiIndexCatalogResult([], null, "build-1"),
            new ApiIndexSelection(
                CodebaseKind.S1MApi,
                CodeChannel.Release,
                ApiIndexAvailability.Unavailable,
                null,
                null,
                null,
                null,
                "No completed S1MApi Release API index is available."));

        Assert.NotNull(envelope.Error);
        Assert.Equal("NoCompletedIndex", envelope.Error.Code);
        Assert.Null(envelope.Error.Hint);
    }
}
