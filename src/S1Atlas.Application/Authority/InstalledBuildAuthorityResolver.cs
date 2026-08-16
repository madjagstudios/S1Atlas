using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Authority;

namespace S1Atlas.Application.Authority;

public sealed class InstalledBuildAuthorityResolver
{
    private readonly PreferredVerifiedExtractionResolver _preferredResolver;
    private readonly IAtlasRepository _atlas;
    private readonly IIndexRepository _index;
    private readonly IValidatedExtractionRepository _validated;

    public InstalledBuildAuthorityResolver(
        PreferredVerifiedExtractionResolver preferredResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        IValidatedExtractionRepository validatedRepository)
    {
        _preferredResolver = preferredResolver ?? throw new ArgumentNullException(nameof(preferredResolver));
        _atlas = atlasRepository ?? throw new ArgumentNullException(nameof(atlasRepository));
        _index = indexRepository ?? throw new ArgumentNullException(nameof(indexRepository));
        _validated = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
    }

    public Task<PreferredVerifiedExtraction?> ResolvePreferredExtractionAsync(
        string buildId,
        CancellationToken ct) =>
        _preferredResolver.ResolveAsync(buildId, ct);

    public async Task<InstalledBuildAuthority> ResolveAsync(
        string? requestedBuildId,
        CancellationToken ct)
    {
        string resolvedBuildId;
        if (string.IsNullOrWhiteSpace(requestedBuildId))
        {
            var current = await _atlas.GetCurrentSnapshotAsync(ct);
            if (current is null)
            {
                return Fail(
                    InstalledBuildAuthorityStatus.NoCurrentBuild,
                    requestedBuildId,
                    null,
                    "No current environment snapshot is available.");
            }

            resolvedBuildId = current.Build.BuildId;
        }
        else
        {
            var builds = await _atlas.ListBuildsAsync(ct);
            if (!builds.Any(build =>
                    string.Equals(build.BuildId, requestedBuildId, StringComparison.Ordinal)))
            {
                return Fail(
                    InstalledBuildAuthorityStatus.BuildNotFound,
                    requestedBuildId,
                    null,
                    "The requested build is not indexed.");
            }

            resolvedBuildId = requestedBuildId;
        }

        var preferred = await _preferredResolver.ResolveAsync(resolvedBuildId, ct);
        if (preferred is null)
        {
            var preferenceRow = await _validated.GetPreferredExtractionAsync(resolvedBuildId, ct);
            return preferenceRow is null
                ? Fail(
                    InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction,
                    requestedBuildId,
                    resolvedBuildId,
                    "No preferred verified extraction exists for the build.")
                : Fail(
                    InstalledBuildAuthorityStatus.ExtractionIntegrityFailure,
                    requestedBuildId,
                    resolvedBuildId,
                    "The preferred extraction failed integrity verification.");
        }

        if (!string.Equals(preferred.Extraction.BuildId, resolvedBuildId, StringComparison.Ordinal))
        {
            return Fail(
                InstalledBuildAuthorityStatus.IndexBuildMismatch,
                requestedBuildId,
                resolvedBuildId,
                "The preferred extraction does not belong to the resolved build.");
        }

        var extractionId = preferred.Extraction.ExtractionId;
        var run = await _index.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            ct);
        if (run is null)
        {
            return Fail(
                InstalledBuildAuthorityStatus.NoCompletedIndex,
                requestedBuildId,
                resolvedBuildId,
                "No completed Schedule I Installed index exists for the verified extraction.");
        }

        var snapshot = await _index.GetCodeSnapshotAsync(run.SnapshotId, ct);
        if (snapshot is null ||
            snapshot.Codebase != CodebaseKind.ScheduleI ||
            snapshot.Channel != CodeChannel.Installed ||
            !string.Equals(snapshot.SourceIdentity, extractionId, StringComparison.Ordinal))
        {
            return Fail(
                InstalledBuildAuthorityStatus.IndexBuildMismatch,
                requestedBuildId,
                resolvedBuildId,
                "The completed index does not match the preferred extraction authority.");
        }

        var associatedBuildId = await _index.GetCompletedIndexBuildIdAsync(run.IndexId, ct);
        if (associatedBuildId is not null &&
            !string.Equals(associatedBuildId, resolvedBuildId, StringComparison.Ordinal))
        {
            return Fail(
                InstalledBuildAuthorityStatus.IndexBuildMismatch,
                requestedBuildId,
                resolvedBuildId,
                "The completed index does not belong to the resolved build.");
        }

        return new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved,
            requestedBuildId,
            resolvedBuildId,
            extractionId,
            run.IndexId,
            run,
            null);
    }

    private static InstalledBuildAuthority Fail(
        InstalledBuildAuthorityStatus status,
        string? requestedBuildId,
        string? resolvedBuildId,
        string message) =>
        new(status, requestedBuildId, resolvedBuildId, null, null, null, message);
}
