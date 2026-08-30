using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class ApiIndexQueryService
{
    private const int MaxSourceNeighborhoodLimit = 50;

    private static readonly (CodebaseKind Codebase, CodeChannel Channel)[] ApiScopes =
    [
        (CodebaseKind.S1Api, CodeChannel.Installed),
        (CodebaseKind.S1Api, CodeChannel.Release),
        (CodebaseKind.S1Api, CodeChannel.Preview),
        (CodebaseKind.S1MApi, CodeChannel.Installed),
        (CodebaseKind.S1MApi, CodeChannel.Release),
        (CodebaseKind.S1MApi, CodeChannel.Preview)
    ];

    private readonly IIndexRepository _repository;
    private readonly IAtlasRepository? _atlasRepository;
    private readonly IndexQueryService _indexQueryService;

    public ApiIndexQueryService(
        IIndexRepository repository,
        IndexQueryService indexQueryService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _indexQueryService = indexQueryService ?? throw new ArgumentNullException(nameof(indexQueryService));
        _atlasRepository = repository as IAtlasRepository;
    }

    public async Task<ApiIndexCatalogResult> ListAsync(
        string? buildId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authority = await ResolveBuildAsync(buildId, cancellationToken);
        var selections = new List<ApiIndexSelection>(ApiScopes.Length);

        foreach (var (codebase, channel) in ApiScopes)
        {
            var resolved = await ResolveSelectionAsync(
                codebase,
                channel,
                authority,
                cancellationToken);
            selections.Add(resolved.Selection);
        }

        return new ApiIndexCatalogResult(
            selections,
            buildId,
            authority.ResolvedBuildId);
    }

    public async Task<SymbolSearchResult> SearchAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateApiScope(codebase, channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateLimit(limit, nameof(limit));

        var authority = await ResolveBuildAsync(null, cancellationToken);
        var selection = await ResolveSelectionAsync(codebase, channel, authority, cancellationToken);
        return await SearchSelectedAsync(
            selection.Selection,
            selector,
            limit,
            cancellationToken);
    }

    public async Task<SymbolSearchResult> SearchSelectedAsync(
        ApiIndexSelection selection,
        string selector,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateApiScope(selection.Codebase, selection.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateLimit(limit, nameof(limit));

        var run = await GetSelectedRunAsync(selection, cancellationToken);
        if (run is null)
        {
            return new SymbolSearchResult(
                0,
                0,
                [],
                SymbolResolutionStatus.NoCompletedIndex);
        }

        return await _indexQueryService.SearchInIndexAsync(
            run,
            selection.Codebase,
            selection.Channel,
            selector,
            limit,
            kind: null,
            cancellationToken);
    }

    public async Task<SourceSnippetResolutionResult> SourceAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int context,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        ValidateApiScope(codebase, channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
        if (relatedLimit < 0 || relatedLimit > MaxSourceNeighborhoodLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relatedLimit),
                $"The source neighborhood limit must be between 0 and {MaxSourceNeighborhoodLimit}.");
        }

        var authority = await ResolveBuildAsync(null, cancellationToken);
        var selection = await ResolveSelectionAsync(codebase, channel, authority, cancellationToken);
        return await SourceSelectedAsync(
            selection.Selection,
            selector,
            context,
            relatedLimit,
            cancellationToken);
    }

    public async Task<SourceSnippetResolutionResult> SourceSelectedAsync(
        ApiIndexSelection selection,
        string selector,
        int context,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateApiScope(selection.Codebase, selection.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
        if (relatedLimit < 0 || relatedLimit > MaxSourceNeighborhoodLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relatedLimit),
                $"The source neighborhood limit must be between 0 and {MaxSourceNeighborhoodLimit}.");
        }

        var run = await GetSelectedRunAsync(selection, cancellationToken);
        if (run is null)
        {
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                null);
        }

        return await _indexQueryService.SourceInIndexAsync(
            run,
            selection.Codebase,
            selection.Channel,
            selector,
            context,
            cancellationToken,
            relatedLimit: relatedLimit);
    }

    public async Task<RelationshipQuerySetResult> RelationshipsSelectedAsync(
        ApiIndexSelection selection,
        string selector,
        int limit,
        ApiRelationshipDirection direction,
        IReadOnlySet<string>? relationshipKinds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateApiScope(selection.Codebase, selection.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateLimit(limit, nameof(limit));

        var run = await GetSelectedRunAsync(selection, cancellationToken);
        if (run is null)
        {
            return new RelationshipQuerySetResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                [],
                null,
                direction == ApiRelationshipDirection.Callers,
                string.Empty);
        }

        return direction switch
        {
            ApiRelationshipDirection.References when relationshipKinds is not null =>
                await _indexQueryService.RelatedTypesInIndexAsync(
                    run,
                    selection.Codebase,
                    selection.Channel,
                    selector,
                    limit,
                    relationshipKinds,
                    cancellationToken),
            ApiRelationshipDirection.Callers => await _indexQueryService.CallersInIndexAsync(
                run, selection.Codebase, selection.Channel, selector, limit, cancellationToken),
            ApiRelationshipDirection.Callees => await _indexQueryService.CalleesInIndexAsync(
                run, selection.Codebase, selection.Channel, selector, limit, cancellationToken),
            _ => await _indexQueryService.RefsInIndexAsync(
                run, selection.Codebase, selection.Channel, selector, limit, cancellationToken)
        };
    }

    public async Task<CallSiteQueryResult> CallSitesSelectedAsync(
        ApiIndexSelection selection,
        string selector,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateApiScope(selection.Codebase, selection.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateLimit(limit, nameof(limit));

        var run = await GetSelectedRunAsync(selection, cancellationToken);
        return run is null
            ? new CallSiteQueryResult(new RelationshipQueryPageResult(0, 0, []), string.Empty)
            : await _indexQueryService.CallSitesInIndexAsync(
                run, selection.Codebase, selection.Channel, selector, limit, cancellationToken);
    }

    public async Task<FieldReferenceQueryResult> FieldReferencesSelectedAsync(
        ApiIndexSelection selection,
        string selector,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateApiScope(selection.Codebase, selection.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateLimit(limit, nameof(limit));

        var run = await GetSelectedRunAsync(selection, cancellationToken);
        return run is null
            ? new FieldReferenceQueryResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                new RelationshipQueryPageResult(0, 0, []))
            : await _indexQueryService.FieldReferencesInIndexAsync(
                run, selection.Codebase, selection.Channel, selector, limit, filter, cancellationToken);
    }

    private async Task<IndexRunRecord?> GetSelectedRunAsync(
        ApiIndexSelection selection,
        CancellationToken cancellationToken)
    {
        if (selection.Availability != ApiIndexAvailability.Current ||
            string.IsNullOrWhiteSpace(selection.IndexId) ||
            string.IsNullOrWhiteSpace(selection.SnapshotId))
        {
            return null;
        }

        var run = await _repository.GetCompletedIndexAsync(selection.IndexId, cancellationToken);
        if (run is null || !string.Equals(run.SnapshotId, selection.SnapshotId, StringComparison.Ordinal))
            return null;

        return await GetMatchingSnapshotAsync(
            run,
            selection.Codebase,
            selection.Channel,
            cancellationToken) is null
            ? null
            : run;
    }

    private async Task<ResolvedApiSelection> ResolveSelectionAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        BuildAuthority authority,
        CancellationToken cancellationToken)
    {
        if (channel is CodeChannel.Release or CodeChannel.Preview)
            return await ResolveUpstreamSelectionAsync(codebase, channel, cancellationToken);

        return await ResolveInstalledSelectionAsync(codebase, authority, cancellationToken);
    }

    private async Task<ResolvedApiSelection> ResolveUpstreamSelectionAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        CancellationToken cancellationToken)
    {
        var run = await _repository.GetLatestCompletedIndexAsync(
            codebase,
            channel,
            environmentSnapshotId: null,
            cancellationToken);
        if (run is null)
        {
            return Unavailable(
                codebase,
                channel,
                $"No completed {codebase} {channel} API index is available.");
        }

        var snapshot = await GetMatchingSnapshotAsync(run, codebase, channel, cancellationToken);
        if (snapshot is null)
        {
            return Unavailable(
                codebase,
                channel,
                "The completed API index has no matching code snapshot.");
        }

        return new ResolvedApiSelection(
            new ApiIndexSelection(
                codebase,
                channel,
                ApiIndexAvailability.Current,
                run.IndexId,
                run.SnapshotId,
                snapshot.SourceIdentity,
                snapshot.EnvironmentSnapshotId,
                "Completed upstream API source is available; no Schedule I build binding applies."),
            run);
    }

    private async Task<ResolvedApiSelection> ResolveInstalledSelectionAsync(
        CodebaseKind codebase,
        BuildAuthority authority,
        CancellationToken cancellationToken)
    {
        if (!authority.IsUsable)
        {
            var unscoped = await _repository.GetLatestCompletedIndexAsync(
                codebase,
                CodeChannel.Installed,
                environmentSnapshotId: null,
                cancellationToken);
            if (unscoped is null)
            {
                return Unavailable(
                    codebase,
                    CodeChannel.Installed,
                    authority.Message ?? $"No completed {codebase} Installed API index is available.");
            }

            var unscopedSnapshot = await GetMatchingSnapshotAsync(
                unscoped,
                codebase,
                CodeChannel.Installed,
                cancellationToken);
            return unscopedSnapshot is null
                ? Unavailable(
                    codebase,
                    CodeChannel.Installed,
                    "The completed API index has no matching code snapshot.")
                : new ResolvedApiSelection(
                    new ApiIndexSelection(
                        codebase,
                        CodeChannel.Installed,
                        ApiIndexAvailability.Unavailable,
                        unscoped.IndexId,
                        unscoped.SnapshotId,
                        unscopedSnapshot.SourceIdentity,
                        unscopedSnapshot.EnvironmentSnapshotId,
                        authority.Message ?? "Installed API build authority is unavailable."),
                    Run: null);
        }

        var current = authority.EnvironmentSnapshotId is null
            ? await _repository.GetLatestCompletedIndexForBuildAsync(
                codebase,
                CodeChannel.Installed,
                authority.ResolvedBuildId!,
                cancellationToken)
            : await _repository.GetLatestCompletedIndexAsync(
                codebase,
                CodeChannel.Installed,
                authority.EnvironmentSnapshotId,
                cancellationToken);
        if (current is not null)
        {
            var currentSnapshot = await GetMatchingSnapshotAsync(
                current,
                codebase,
                CodeChannel.Installed,
                cancellationToken);
            if (currentSnapshot is not null)
            {
                return new ResolvedApiSelection(
                    new ApiIndexSelection(
                        codebase,
                        CodeChannel.Installed,
                        ApiIndexAvailability.Current,
                        current.IndexId,
                        current.SnapshotId,
                        currentSnapshot.SourceIdentity,
                        currentSnapshot.EnvironmentSnapshotId,
                        "Installed API index matches the selected Schedule I build."),
                    current);
            }
        }

        var latest = await _repository.GetLatestCompletedIndexAsync(
            codebase,
            CodeChannel.Installed,
            environmentSnapshotId: null,
            cancellationToken);
        if (latest is null)
        {
            return Unavailable(
                codebase,
                CodeChannel.Installed,
                $"No completed {codebase} Installed API index is available for the selected build.");
        }

        var latestSnapshot = await GetMatchingSnapshotAsync(
            latest,
            codebase,
            CodeChannel.Installed,
            cancellationToken);
        if (latestSnapshot is null)
        {
            return Unavailable(
                codebase,
                CodeChannel.Installed,
                "The completed API index has no matching code snapshot.");
        }

        var associatedBuildId = await _repository.GetCompletedIndexBuildIdAsync(
            latest.IndexId,
            cancellationToken);
        var associationMessage = associatedBuildId is null
            ? "The completed installed API index is not bound to the selected Schedule I build."
            : $"The completed installed API index is bound to build '{associatedBuildId}', not selected build '{authority.ResolvedBuildId}'.";
        return new ResolvedApiSelection(
            new ApiIndexSelection(
                codebase,
                CodeChannel.Installed,
                ApiIndexAvailability.Stale,
                latest.IndexId,
                latest.SnapshotId,
                latestSnapshot.SourceIdentity,
                latestSnapshot.EnvironmentSnapshotId,
                associationMessage),
            latest);
    }

    private async Task<BuildAuthority> ResolveBuildAsync(
        string? requestedBuildId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedBuildId))
        {
            if (_atlasRepository is null)
            {
                return new BuildAuthority(
                    requestedBuildId,
                    ResolvedBuildId: null,
                    IsUsable: false,
                    Message: "The requested Schedule I build cannot be verified by this index repository.",
                    EnvironmentSnapshotId: null);
            }

            var builds = await _atlasRepository.ListBuildsAsync(cancellationToken);
            if (builds.Any(build => string.Equals(build.BuildId, requestedBuildId, StringComparison.Ordinal)))
            {
                return new BuildAuthority(
                    requestedBuildId,
                    requestedBuildId,
                    IsUsable: true,
                    Message: null,
                    EnvironmentSnapshotId: null);
            }

            return new BuildAuthority(
                requestedBuildId,
                ResolvedBuildId: null,
                IsUsable: false,
                Message: $"The requested Schedule I build '{requestedBuildId}' is not indexed.",
                EnvironmentSnapshotId: null);
        }

        if (_atlasRepository is null)
        {
            return new BuildAuthority(
                RequestedBuildId: null,
                ResolvedBuildId: null,
                IsUsable: false,
                Message: "No current Schedule I build authority is available.",
                EnvironmentSnapshotId: null);
        }

        var current = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        return current is null
            ? new BuildAuthority(
                RequestedBuildId: null,
                ResolvedBuildId: null,
                IsUsable: false,
                Message: "No current Schedule I build authority is available.",
                EnvironmentSnapshotId: null)
            : new BuildAuthority(
                RequestedBuildId: null,
                ResolvedBuildId: current.Build.BuildId,
                IsUsable: true,
                Message: null,
                EnvironmentSnapshotId: current.SnapshotId);
    }

    private async Task<CodeSnapshotRecord?> GetMatchingSnapshotAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        CancellationToken cancellationToken)
    {
        if (run.Status != IndexRunStatus.Completed)
            return null;

        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        return snapshot is not null &&
               snapshot.Codebase == codebase &&
               snapshot.Channel == channel
            ? snapshot
            : null;
    }

    private static ResolvedApiSelection Unavailable(
        CodebaseKind codebase,
        CodeChannel channel,
        string message) =>
        new(
            new ApiIndexSelection(
                codebase,
                channel,
                ApiIndexAvailability.Unavailable,
                null,
                null,
                null,
                null,
                message),
            Run: null);

    private static void ValidateApiScope(CodebaseKind codebase, CodeChannel channel)
    {
        if (codebase is not (CodebaseKind.S1Api or CodebaseKind.S1MApi))
        {
            throw new ArgumentException(
                "API queries support only S1Api and S1MApi codebases.",
                nameof(codebase));
        }

        if (channel is not (CodeChannel.Installed or CodeChannel.Release or CodeChannel.Preview))
        {
            throw new ArgumentException(
                "API queries support only Installed, Release, and Preview channels.",
                nameof(channel));
        }
    }

    private static void ValidateLimit(int limit, string parameterName)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "The query result limit must be positive.");
    }

    private sealed record BuildAuthority(
        string? RequestedBuildId,
        string? ResolvedBuildId,
        bool IsUsable,
        string? Message,
        string? EnvironmentSnapshotId);

    private sealed record ResolvedApiSelection(
        ApiIndexSelection Selection,
        IndexRunRecord? Run);
}

public enum ApiRelationshipDirection
{
    References,
    Callers,
    Callees
}
