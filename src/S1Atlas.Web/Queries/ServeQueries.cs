using S1Atlas.Application.Authority;
using S1Atlas.Application.Composition;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Web.Queries;

public enum ServeRelationshipDirection
{
    Callers,
    Callees,
    References,
    Overrides,
    OverriddenBy,
    Derived
}

public sealed record ServeIndex(
    CodebaseKind Codebase,
    CodeChannel Channel,
    string IndexId,
    IndexRunRecord Run,
    ApiIndexSelection? ApiSelection);

// Read-only query facade over the shared atlas composition. A missing data
// store surfaces as AtlasStoreMissingException so endpoints can answer with a
// clear message instead of a 500.
public sealed class ServeQueries
{
    internal const int MemberSearchLimit = 500;
    private const int HierarchyDepth = 10;

    private readonly AtlasReadOnlyServices _services;
    private readonly ApiIndexQueryService _api;
    private readonly DiffResultCache _diffCache;

    public ServeQueries(AtlasReadOnlyServices services, DiffResultCache? diffCache = null)
    {
        _services = services;
        _api = new ApiIndexQueryService(services.Repository, services.IndexQueryService);
        _diffCache = diffCache ?? new DiffResultCache();
    }

    public Task<InstalledBuildAuthority> ResolveAuthorityAsync(
        CancellationToken ct,
        string? buildId = null) =>
        WithStoreAsync(token => _services.AuthorityResolver.ResolveAsync(buildId, token), ct);

    public Task<InstalledBuildHistoryResult> GetHistoryAsync(CancellationToken ct) =>
        WithStoreAsync(token => _services.InstalledBuildHistoryQueryService.GetHistoryAsync(token), ct);

    public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(CancellationToken ct) =>
        WithStoreAsync(token => _services.Repository.GetCurrentSnapshotAsync(token), ct);

    public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(CancellationToken ct) =>
        WithStoreAsync(token => _services.Repository.ListBuildsAsync(token), ct);

    public Task<PreferredVerifiedExtraction?> ResolvePreferredExtractionAsync(
        string buildId,
        CancellationToken ct) =>
        WithStoreAsync(token => _services.AuthorityResolver.ResolvePreferredExtractionAsync(buildId, token), ct);

    public Task<BuildDiffResult> DiffAsync(
        string indexIdA,
        string indexIdB,
        string? kindFilter,
        CancellationToken ct) =>
        _diffCache.GetOrAddAsync(
            new DiffCacheKey(indexIdA, indexIdB, "ScheduleI", kindFilter),
            () => WithStoreAsync(
                token => _services.BuildDiffService.DiffAsync(
                    indexIdA, indexIdB, "ScheduleI", "Installed", kindFilter, token),
                ct),
            ct);

    public Task<IReadOnlyList<SymbolQueryResult>> GetCanonicalSymbolsAsync(
        IndexRunRecord run,
        string canonicalKey,
        CancellationToken ct,
        CodebaseKind codebase = CodebaseKind.ScheduleI,
        CodeChannel channel = CodeChannel.Installed) =>
        WithStoreAsync(
            token => _services.IndexQueryService.GetCanonicalSymbolsInIndexAsync(
                run, codebase, channel, canonicalKey, token),
            ct);

    public Task<ApiIndexCatalogResult> ListApiCatalogAsync(CancellationToken ct) =>
        WithStoreAsync(token => _api.ListAsync(null, token), ct);

    public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken ct) =>
        WithStoreAsync(token => _services.Repository.GetCompletedIndexAsync(indexId, token), ct);

    public Task<int> CountSymbolsAsync(string indexId, CancellationToken ct) =>
        WithStoreAsync(token => _services.Repository.CountCompletedSymbolsAsync(indexId, token), ct);

    public async Task<SymbolSearchResult> SearchGameAsync(
        IndexRunRecord run,
        string query,
        SymbolKind? kind,
        int limit,
        CancellationToken ct,
        bool includeGenerated = false)
    {
        var result = await WithStoreAsync(
            token => _services.IndexQueryService.SearchRankedInIndexAsync(
                run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, kind, token, includeGenerated),
            ct);
        return result with { SearchNotice = RewordSearchNotice(result.SearchNotice) };
    }

    public async Task<SymbolSearchResult> SearchApiAsync(
        ApiIndexSelection selection,
        string query,
        int limit,
        CancellationToken ct,
        bool includeGenerated = false)
    {
        var result = await WithStoreAsync(
            token => _api.SearchRankedSelectedAsync(selection, query, limit, token, includeGenerated),
            ct);
        return result with { SearchNotice = RewordSearchNotice(result.SearchNotice) };
    }

    private static string? RewordSearchNotice(string? notice) =>
        notice?.Replace("includeGenerated", "?generated=1", StringComparison.Ordinal);

    public Task<SymbolQueryResult?> GetSymbolAsync(string indexId, string symbolId, CancellationToken ct) =>
        WithStoreAsync(
            token => _services.IndexQueryService.GetExactSymbolAsync(indexId, symbolId, token),
            ct);

    public async Task<MemberListResult> GetMembersAsync(
        ServeIndex index,
        SymbolQueryResult type,
        CancellationToken ct)
    {
        var result = index.ApiSelection is { } selection
            ? await SearchApiAsync(selection, type.QualifiedName + ".", MemberSearchLimit, ct)
            : await SearchGameAsync(index.Run, type.QualifiedName + ".", null, MemberSearchLimit, ct);
        var members = result.Results.Where(candidate => IsMemberOf(candidate, type)).ToArray();
        return new MemberListResult(members, result.TotalCount, result.ReturnedCount < result.TotalCount);
    }

    public Task<SourceSnippetResolutionResult> GetSourceAsync(
        ServeIndex index,
        string symbolId,
        bool fullType,
        CancellationToken ct) =>
        index.ApiSelection is { } selection
            ? WithStoreAsync(
                token => _api.SourceSelectedAsync(selection, symbolId, 5, 10, token),
                ct)
            : WithStoreAsync(
                token => _services.IndexQueryService.SourceInIndexAsync(
                    index.Run, index.Codebase, index.Channel, symbolId, 5, token, fullType, 10),
                ct);

    public Task<RelationshipQuerySetResult> GetRelationshipsAsync(
        ServeIndex index,
        string symbolId,
        ServeRelationshipDirection direction,
        int limit,
        CancellationToken ct,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
    {
        if (direction is ServeRelationshipDirection.Overrides or ServeRelationshipDirection.OverriddenBy or ServeRelationshipDirection.Derived)
            return GetHierarchyAsync(index, symbolId, direction, limit, ct);

        if (index.ApiSelection is { } selection)
        {
            var apiDirection = direction switch
            {
                ServeRelationshipDirection.Callers => ApiRelationshipDirection.Callers,
                ServeRelationshipDirection.Callees => ApiRelationshipDirection.Callees,
                _ => ApiRelationshipDirection.References
            };
            return WithStoreAsync(
                token => _api.RelationshipsSelectedAsync(selection, symbolId, limit, apiDirection, null, token, exact, includeGenerated, includeDelegates),
                ct);
        }

        return WithStoreAsync(
            token => direction switch
            {
                ServeRelationshipDirection.Callers => _services.IndexQueryService.CallersInIndexAsync(
                    index.Run, index.Codebase, index.Channel, symbolId, limit, token, exact, includeGenerated, includeDelegates),
                ServeRelationshipDirection.Callees => _services.IndexQueryService.CalleesInIndexAsync(
                    index.Run, index.Codebase, index.Channel, symbolId, limit, token, includeGenerated, includeDelegates),
                _ => _services.IndexQueryService.RefsInIndexAsync(
                    index.Run, index.Codebase, index.Channel, symbolId, limit, token, includeGenerated)
            },
            ct);
    }

    private Task<RelationshipQuerySetResult> GetHierarchyAsync(
        ServeIndex index,
        string symbolId,
        ServeRelationshipDirection direction,
        int limit,
        CancellationToken ct)
    {
        if (index.ApiSelection is not null)
        {
            return Task.FromResult(new RelationshipQuerySetResult(
                new SymbolResolutionResult(SymbolResolutionStatus.Resolved, null, []),
                [],
                null,
                false,
                string.Empty,
                0));
        }

        return WithStoreAsync(
            async token =>
            {
                var hierarchy = direction switch
                {
                    ServeRelationshipDirection.Overrides => await _services.IndexQueryService.OverridesInIndexAsync(
                        index.Run, index.Codebase, index.Channel, symbolId, limit, token),
                    ServeRelationshipDirection.OverriddenBy => await _services.IndexQueryService.OverriddenByInIndexAsync(
                        index.Run, index.Codebase, index.Channel, symbolId, limit, HierarchyDepth, token),
                    _ => await _services.IndexQueryService.DerivedInIndexAsync(
                        index.Run, index.Codebase, index.Channel, symbolId, limit, HierarchyDepth, 0, token)
                };
                return new RelationshipQuerySetResult(
                    hierarchy.Resolution,
                    hierarchy.Nodes.Select(node => node.Edge).ToArray(),
                    null,
                    false,
                    string.Empty,
                    hierarchy.TotalCount);
            },
            ct);
    }

    internal static bool IsMemberOf(SymbolQueryResult candidate, SymbolQueryResult type)
    {
        if (candidate.SymbolId.Equals(type.SymbolId, StringComparison.Ordinal)
            || candidate.Kind.Equals("Type", StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = type.QualifiedName + ".";
        return candidate.QualifiedName.StartsWith(prefix, StringComparison.Ordinal)
            && !candidate.QualifiedName.AsSpan(prefix.Length).Contains('.');
    }

    private static async Task<T> WithStoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            return await operation(ct);
        }
        catch (FileNotFoundException)
        {
            throw new AtlasStoreMissingException();
        }
        catch (DirectoryNotFoundException)
        {
            throw new AtlasStoreMissingException();
        }
    }
}

public sealed record MemberListResult(
    IReadOnlyList<SymbolQueryResult> Members,
    int TotalCount,
    bool Truncated);
