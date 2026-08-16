using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public void Unavailable_CarriesErrorAndNoData()
    {
        var envelope = ToolEnvelope<string>.Unavailable(
            new ToolError("NoCurrentBuild", "No current build."),
            build: null);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Null(envelope.Data);
        Assert.Equal("NoCurrentBuild", envelope.Error!.Code);
    }

    [Fact]
    public void Resolved_EmitsFactProvenanceAndEmptyCandidates()
    {
        var build = new BuildContext(null, "b", "e", "i", "ScheduleI", "Installed", true);
        var envelope = ToolEnvelope<string>.Resolved(
            build,
            "data");

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Same(build, envelope.Build);
        Assert.Equal("data", envelope.Data);
        Assert.Empty(envelope.Candidates);
        Assert.NotEmpty(envelope.Provenance);
        var provenance = Assert.Single(envelope.Provenance);
        Assert.Equal(ProvenanceClassification.Fact, provenance.Classification);
        Assert.Equal("installed-build-authority", provenance.Source);
        Assert.Equal("b", provenance.BuildId);
        Assert.Equal("e", provenance.ExtractionId);
        Assert.Equal("i", provenance.IndexId);
    }

    [Fact]
    public void Ambiguous_PreservesCandidates()
    {
        var build = new BuildContext(null, "b", "e", "i", "ScheduleI", "Installed", true);
        var candidates = new object[] { "first", "second" };

        var envelope = ToolEnvelope<string>.Ambiguous(
            build,
            candidates,
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", "b", "e", "i"));

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.Same(candidates, envelope.Candidates);
        Assert.Equal(2, envelope.Candidates.Count);
    }

    [Fact]
    public void AuthorityEnvelope_MapsResolvedAuthorityToResolvedStatus()
    {
        var authority = new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            "requested",
            "resolved",
            "extraction",
            "index",
            IndexRun: null,
            Message: null);

        var envelope = AuthorityEnvelope.From(authority);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Same(authority, envelope.Data);
        Assert.NotEmpty(envelope.Provenance);
        var provenance = Assert.Single(envelope.Provenance);
        Assert.Equal(ProvenanceClassification.Fact, provenance.Classification);
        Assert.Equal("installed-build-authority", provenance.Source);
        Assert.Equal("resolved", provenance.BuildId);
        Assert.Equal("extraction", provenance.ExtractionId);
        Assert.Equal("index", provenance.IndexId);
    }

    [Theory]
    [MemberData(nameof(AuthorityMappings))]
    public void AuthorityEnvelope_MapsAuthorityStatuses(
        InstalledBuildAuthorityStatus authorityStatus,
        ToolStatus expectedStatus,
        string expectedErrorCode)
    {
        var authority = new InstalledBuildAuthority(
            authorityStatus,
            "requested",
            "resolved",
            "extraction",
            "index",
            IndexRun: null,
            Message: "message");

        var envelope = AuthorityEnvelope.From(authority);

        Assert.Equal(expectedStatus, envelope.Status);
        Assert.Equal(expectedErrorCode, envelope.Error?.Code);
    }

    public static TheoryData<InstalledBuildAuthorityStatus, ToolStatus, string> AuthorityMappings => new()
    {
        { InstalledBuildAuthorityStatus.NoCurrentBuild, ToolStatus.Unavailable, "NoCurrentBuild" },
        { InstalledBuildAuthorityStatus.BuildNotFound, ToolStatus.Invalid, "BuildNotFound" },
        { InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction, ToolStatus.NotFound, "NoPreferredVerifiedExtraction" },
        { InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, ToolStatus.Unavailable, "ExtractionIntegrityFailure" },
        { InstalledBuildAuthorityStatus.NoCompletedIndex, ToolStatus.NotFound, "NoCompletedIndex" },
        { InstalledBuildAuthorityStatus.IndexBuildMismatch, ToolStatus.Invalid, "IndexBuildMismatch" }
    };
}
