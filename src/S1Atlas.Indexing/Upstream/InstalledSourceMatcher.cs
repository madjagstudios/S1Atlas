using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Upstream;

public enum UpstreamMatchKind
{
    ExactSourceCommit,
    ExactBinary,
    ExactReviewedTag,
    SemanticVersion,
    Unmatched
}

public sealed record UpstreamMatchResult(UpstreamMatchKind Kind, string? CommitSha, string Explanation);

public sealed class InstalledSourceMatcher
{
    public UpstreamMatchResult Match(
        DependencyVersion installed,
        string? embeddedCommit,
        IReadOnlyDictionary<string, string> reviewedBinaryHashes,
        IReadOnlyDictionary<string, string> reviewedVersions,
        IReadOnlyDictionary<string, string>? reviewedTags = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(reviewedBinaryHashes);
        ArgumentNullException.ThrowIfNull(reviewedVersions);
        if (!string.IsNullOrWhiteSpace(installed.BinarySha256) && reviewedBinaryHashes.TryGetValue(installed.BinarySha256, out var binaryCommit)) return new UpstreamMatchResult(UpstreamMatchKind.ExactBinary, binaryCommit, "The installed binary hash matches a reviewed upstream artifact.");
        if (reviewedTags is not null && !string.IsNullOrWhiteSpace(installed.Version) && reviewedTags.TryGetValue(installed.Version, out var tagCommit)) return new UpstreamMatchResult(UpstreamMatchKind.ExactReviewedTag, tagCommit, "The installed version matches an exact reviewed upstream tag.");
        if (!string.IsNullOrWhiteSpace(embeddedCommit)) return new UpstreamMatchResult(UpstreamMatchKind.ExactSourceCommit, embeddedCommit, "The installed binary reported an embedded source commit; binary identity was not independently verified.");
        if (!string.IsNullOrWhiteSpace(installed.Version) && reviewedVersions.TryGetValue(installed.Version, out var versionCommit)) return new UpstreamMatchResult(UpstreamMatchKind.SemanticVersion, versionCommit, "The installed version matches a reviewed semantic version; this is not binary proof.");
        return new UpstreamMatchResult(UpstreamMatchKind.Unmatched, null, "No conservative upstream match was found.");
    }
}
