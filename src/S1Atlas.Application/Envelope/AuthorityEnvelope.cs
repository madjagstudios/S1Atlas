using S1Atlas.Application.Authority;

namespace S1Atlas.Application.Envelope;

public static class AuthorityEnvelope
{
    public static ToolEnvelope<InstalledBuildAuthority> From(InstalledBuildAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);

        var build = authority.ResolvedBuildId is null && authority.RequestedBuildId is null
            ? null
            : new BuildContext(
                authority.RequestedBuildId,
                authority.ResolvedBuildId,
                authority.ExtractionId,
                authority.IndexId,
                "ScheduleI",
                "Installed",
                authority.Status == InstalledBuildAuthorityStatus.Resolved);

        return authority.Status switch
        {
            InstalledBuildAuthorityStatus.Resolved =>
                ToolEnvelope<InstalledBuildAuthority>.Resolved(
                    build,
                    authority,
                    CreateFactProvenance(authority)),
            InstalledBuildAuthorityStatus.NoCurrentBuild =>
                ToolEnvelope<InstalledBuildAuthority>.Unavailable(
                    new ToolError("NoCurrentBuild", authority.Message ?? "No current build."),
                    build),
            InstalledBuildAuthorityStatus.BuildNotFound =>
                ToolEnvelope<InstalledBuildAuthority>.Invalid(
                    new ToolError("BuildNotFound", authority.Message ?? "The requested build is not indexed."),
                    build),
            InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction =>
                ToolEnvelope<InstalledBuildAuthority>.NotFound(
                    build,
                    new ToolError("NoPreferredVerifiedExtraction", authority.Message ?? "No preferred verified extraction exists for the build."),
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived,
                        "installed-build-authority",
                        authority.ResolvedBuildId,
                        authority.ExtractionId,
                        authority.IndexId)),
            InstalledBuildAuthorityStatus.ExtractionIntegrityFailure =>
                ToolEnvelope<InstalledBuildAuthority>.Unavailable(
                    new ToolError("ExtractionIntegrityFailure", authority.Message ?? "The preferred extraction failed integrity verification."),
                    build),
            InstalledBuildAuthorityStatus.NoCompletedIndex =>
                ToolEnvelope<InstalledBuildAuthority>.NotFound(
                    build,
                    new ToolError("NoCompletedIndex", authority.Message ?? "No completed Schedule I Installed index exists for the verified extraction."),
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived,
                        "installed-build-authority",
                        authority.ResolvedBuildId,
                        authority.ExtractionId,
                        authority.IndexId)),
            InstalledBuildAuthorityStatus.IndexBuildMismatch =>
                ToolEnvelope<InstalledBuildAuthority>.Invalid(
                    new ToolError("IndexBuildMismatch", authority.Message ?? "The preferred extraction does not belong to the resolved build."),
                    build),
            _ => throw new ArgumentOutOfRangeException(nameof(authority))
        };
    }

    private static ProvenanceEntry CreateFactProvenance(InstalledBuildAuthority authority) =>
        new(
            ProvenanceClassification.Fact,
            "installed-build-authority",
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId);
}
