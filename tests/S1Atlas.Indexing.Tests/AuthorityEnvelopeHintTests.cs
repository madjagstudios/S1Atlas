using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Application.Readiness;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class AuthorityEnvelopeHintTests
{
    public static TheoryData<InstalledBuildAuthorityStatus, string?> Statuses => new()
    {
        { InstalledBuildAuthorityStatus.NoCurrentBuild, ReadinessFixCommands.Scan },
        { InstalledBuildAuthorityStatus.BuildNotFound, ReadinessFixCommands.Builds },
        { InstalledBuildAuthorityStatus.AmbiguousBuildPrefix, null },
        { InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction, ReadinessFixCommands.Extract },
        { InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, ReadinessFixCommands.ExtractRetry },
        { InstalledBuildAuthorityStatus.NoCompletedIndex, ReadinessFixCommands.Index },
        { InstalledBuildAuthorityStatus.IndexBuildMismatch, null }
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public void From_PassesAuthorityHintToToolError(
        InstalledBuildAuthorityStatus status,
        string? expectedHint)
    {
        var authority = new InstalledBuildAuthority(
            status,
            "requested",
            "resolved",
            null,
            null,
            null,
            "message",
            expectedHint);

        var envelope = AuthorityEnvelope.From<object>(authority);

        Assert.NotNull(envelope.Error);
        Assert.Equal(status.ToString(), envelope.Error.Code);
        Assert.Equal(expectedHint, envelope.Error.Hint);
    }
}
