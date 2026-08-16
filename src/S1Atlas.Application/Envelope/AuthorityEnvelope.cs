using S1Atlas.Application.Authority;

namespace S1Atlas.Application.Envelope;

public static class AuthorityEnvelope
{
    public static ToolEnvelope<InstalledBuildAuthority> From(InstalledBuildAuthority authority)
    {
        return FromCore(
            authority,
            resolved => ToolEnvelope<InstalledBuildAuthority>.Resolved(
                resolved.build,
                authority,
                resolved.fact));
    }

    public static ToolEnvelope<T> From<T>(InstalledBuildAuthority authority) where T : class
    {
        if (authority.Status == InstalledBuildAuthorityStatus.Resolved)
        {
            throw new InvalidOperationException("Resolved authority envelopes for non-authority payloads are not supported.");
        }

        return FromCore<T>(authority, _ => throw new InvalidOperationException("Unreachable resolved authority state."));
    }

    private static ToolEnvelope<T> FromCore<T>(
        InstalledBuildAuthority authority,
        Func<(BuildContext build, ProvenanceEntry fact), ToolEnvelope<T>> onResolved) where T : class
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
        var fact = CreateFactProvenance(authority);

        return authority.Status switch
        {
            InstalledBuildAuthorityStatus.Resolved when build is not null =>
                onResolved((build, fact)),
            InstalledBuildAuthorityStatus.NoCurrentBuild =>
                ToolEnvelope<T>.Unavailable(
                    new ToolError("NoCurrentBuild", authority.Message ?? "No current build."),
                    build),
            InstalledBuildAuthorityStatus.BuildNotFound =>
                ToolEnvelope<T>.Invalid(
                    new ToolError("BuildNotFound", authority.Message ?? "The requested build is not indexed."),
                    build),
            InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction =>
                ToolEnvelope<T>.NotFound(
                    build,
                    new ToolError("NoPreferredVerifiedExtraction", authority.Message ?? "No preferred verified extraction exists for the build."),
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived,
                        "installed-build-authority",
                        authority.ResolvedBuildId,
                        authority.ExtractionId,
                        authority.IndexId)),
            InstalledBuildAuthorityStatus.ExtractionIntegrityFailure =>
                ToolEnvelope<T>.Unavailable(
                    new ToolError("ExtractionIntegrityFailure", authority.Message ?? "The preferred extraction failed integrity verification."),
                    build),
            InstalledBuildAuthorityStatus.NoCompletedIndex =>
                ToolEnvelope<T>.NotFound(
                    build,
                    new ToolError("NoCompletedIndex", authority.Message ?? "No completed Schedule I Installed index exists for the verified extraction."),
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived,
                        "installed-build-authority",
                        authority.ResolvedBuildId,
                        authority.ExtractionId,
                        authority.IndexId)),
            InstalledBuildAuthorityStatus.IndexBuildMismatch =>
                ToolEnvelope<T>.Invalid(
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
