using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Web.Envelopes;
using S1Atlas.Web.Queries;
using S1Atlas.Web.Rendering;

namespace S1Atlas.Web.Endpoints;

internal sealed record ResolvedSymbolOutcome(
    InstalledBuildAuthority Authority,
    ApiIndexCatalogResult Catalog,
    ServeIndex? Index,
    SymbolQueryResult? Symbol,
    IReadOnlyList<ServeIndex> ApiIndexes);

internal static class SymbolEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/symbol/{id}", ["GET", "HEAD"], SymbolPageAsync);
        app.MapMethods("/api/symbol/{id}", ["GET", "HEAD"], ApiSymbolAsync);
        app.MapMethods("/api/symbol/{id}/callers", ["GET", "HEAD"], (HttpContext c, ServeQueries q, string id, CancellationToken ct) => ApiRelationshipsAsync(c, q, id, ServeRelationshipDirection.Callers, ct));
        app.MapMethods("/api/symbol/{id}/callees", ["GET", "HEAD"], (HttpContext c, ServeQueries q, string id, CancellationToken ct) => ApiRelationshipsAsync(c, q, id, ServeRelationshipDirection.Callees, ct));
        app.MapMethods("/api/symbol/{id}/references", ["GET", "HEAD"], (HttpContext c, ServeQueries q, string id, CancellationToken ct) => ApiRelationshipsAsync(c, q, id, ServeRelationshipDirection.References, ct));
    }

    private static async Task<IResult> SymbolPageAsync(
        HttpContext context,
        ServeQueries queries,
        string id,
        CancellationToken ct)
    {
        ResolvedSymbolOutcome outcome;
        try
        {
            outcome = await ResolveSymbolAsync(queries, id, ct);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(Html.Layout("No atlas", "<h1>No atlas</h1><p>No Atlas data store was found.</p>"));
        }

        if (outcome.Symbol is null || outcome.Index is null)
        {
            return ServeHttp.Html(
                UnknownOrMissingPage(outcome, id),
                HasAnyIndex(outcome) ? StatusCodes.Status404NotFound : StatusCodes.Status200OK);
        }

        var symbol = outcome.Symbol;
        var index = outcome.Index;
        bool exact;
        bool includeGenerated;
        bool includeDelegates;
        try
        {
            exact = QueryBinding.BindExact(context.Request.Query["exact"].ToString());
            includeGenerated = QueryBinding.BindGenerated(context.Request.Query["generated"].ToString());
            includeDelegates = QueryBinding.BindDelegates(context.Request.Query["delegates"].ToString());
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Html(BadQueryPage(exception.Message), StatusCodes.Status400BadRequest);
        }

        var members = symbol.Kind.Equals("Type", StringComparison.Ordinal)
            ? await queries.GetMembersAsync(index, symbol, ct)
            : new MemberListResult([], 0, false);
        var source = await queries.GetSourceAsync(index, symbol.SymbolId, symbol.Kind.Equals("Type", StringComparison.Ordinal), ct);
        var callers = await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.Callers, QueryBinding.DefaultRelationshipLimit, ct, exact, includeGenerated, includeDelegates);
        var callees = await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.Callees, QueryBinding.DefaultRelationshipLimit, ct, includeGenerated: includeGenerated, includeDelegates: includeDelegates);
        var references = await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.References, QueryBinding.DefaultRelationshipLimit, ct, includeGenerated: includeGenerated);
        var overrides = symbol.Kind.Equals("Method", StringComparison.Ordinal)
            ? await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.Overrides, QueryBinding.DefaultRelationshipLimit, ct)
            : EmptyRelationships();
        var overriddenBy = symbol.Kind.Equals("Method", StringComparison.Ordinal)
            ? await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.OverriddenBy, QueryBinding.DefaultRelationshipLimit, ct)
            : EmptyRelationships();
        var derived = symbol.Kind.Equals("Type", StringComparison.Ordinal)
            ? await queries.GetRelationshipsAsync(index, symbol.SymbolId, ServeRelationshipDirection.Derived, QueryBinding.DefaultRelationshipLimit, ct)
            : EmptyRelationships();
        var typeSymbolId = await ResolveDeclaringTypeAsync(queries, index, symbol, ct);
        return ServeHttp.Html(SymbolView.Render(new SymbolModel(
            index, symbol, typeSymbolId, members, source.Snippet, callers, callees, references, overrides, overriddenBy, derived)));
    }

    private static RelationshipQuerySetResult EmptyRelationships() => new(
        new SymbolResolutionResult(SymbolResolutionStatus.Resolved, null, []),
        [],
        null,
        false,
        string.Empty,
        0);

    private static async Task<string?> ResolveDeclaringTypeAsync(
        ServeQueries queries,
        ServeIndex index,
        SymbolQueryResult symbol,
        CancellationToken ct)
    {
        if (symbol.Kind.Equals("Type", StringComparison.Ordinal))
        {
            return null;
        }

        var (_, declaringType, _) = SymbolView.SplitBreadcrumb(symbol.QualifiedName, symbol.Kind);
        var key = $"{symbol.Codebase}:{symbol.Channel}:Type:{CanonicalSignatureRenderer.RenderType(declaringType)}";
        var matches = await queries.GetCanonicalSymbolsAsync(
            index.Run, key, ct, index.Codebase, index.Channel);
        return matches.FirstOrDefault(match =>
            !match.SymbolId.Equals(symbol.SymbolId, StringComparison.Ordinal))?.SymbolId;
    }

    private static async Task<IResult> ApiSymbolAsync(
        ServeQueries queries,
        string id,
        CancellationToken ct)
    {
        ResolvedSymbolOutcome outcome;
        try
        {
            outcome = await ResolveSymbolAsync(queries, id, ct);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<SymbolQueryResult>());
        }

        if (outcome.Symbol is null || outcome.Index is null)
        {
            return ServeHttp.Envelope(MissingSymbolEnvelope(outcome));
        }

        return ServeHttp.Envelope(outcome.Index.ApiSelection is { } selection
            ? ServeEnvelopes.FromApiExactSymbol(outcome.Catalog, selection, outcome.Symbol)
            : ServeEnvelopes.FromExactSymbol(outcome.Authority, outcome.Symbol));
    }

    private static async Task<IResult> ApiRelationshipsAsync(
        HttpContext context,
        ServeQueries queries,
        string id,
        ServeRelationshipDirection direction,
        CancellationToken ct)
    {
        int limit;
        bool exact;
        bool includeGenerated;
        bool includeDelegates;
        try
        {
            limit = QueryBinding.BindLimit(context.Request.Query["limit"].ToString(), QueryBinding.DefaultRelationshipLimit);
            exact = QueryBinding.BindExact(context.Request.Query["exact"].ToString());
            includeGenerated = QueryBinding.BindGenerated(context.Request.Query["generated"].ToString());
            includeDelegates = QueryBinding.BindDelegates(context.Request.Query["delegates"].ToString());
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Envelope(ToolEnvelope<RelationshipQuerySetResult>.Invalid(
                new ToolError("InvalidQuery", exception.Message)));
        }

        ResolvedSymbolOutcome outcome;
        try
        {
            outcome = await ResolveSymbolAsync(queries, id, ct);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<RelationshipQuerySetResult>());
        }

        if (outcome.Symbol is null || outcome.Index is null)
        {
            return ServeHttp.Envelope(MissingRelationshipsEnvelope(outcome));
        }

        var result = await queries.GetRelationshipsAsync(outcome.Index, outcome.Symbol.SymbolId, direction, limit, ct, exact, includeGenerated, includeDelegates);
        return ServeHttp.Envelope(outcome.Index.ApiSelection is { } selection
            ? ServeEnvelopes.FromApiRelationships(outcome.Catalog, selection, result)
            : ServeEnvelopes.FromRelationships(outcome.Authority, result));
    }

    private static async Task<ResolvedSymbolOutcome> ResolveSymbolAsync(
        ServeQueries queries,
        string id,
        CancellationToken ct)
    {
        var authority = await queries.ResolveAuthorityAsync(ct);
        var catalog = await queries.ListApiCatalogAsync(ct);
        var apiIndexes = new List<ServeIndex>();
        foreach (var selection in catalog.Selections)
        {
            if (selection is { Availability: ApiIndexAvailability.Current, IndexId: { } indexId })
            {
                // The run can vanish between the catalog read and this lookup
                // when the atlas changes mid-request; skip it like the search
                // path's missing-index handling instead of failing the page.
                var run = await queries.GetCompletedIndexAsync(indexId, ct);
                if (run is null)
                {
                    continue;
                }

                apiIndexes.Add(new ServeIndex(selection.Codebase, selection.Channel, indexId, run, selection));
            }
        }

        if (authority.Status == InstalledBuildAuthorityStatus.Resolved && authority.IndexRun is { } gameRun)
        {
            var symbol = await queries.GetSymbolAsync(gameRun.IndexId, id, ct);
            if (symbol is not null)
            {
                var game = new ServeIndex(CodebaseKind.ScheduleI, CodeChannel.Installed, gameRun.IndexId, gameRun, null);
                return new ResolvedSymbolOutcome(authority, catalog, game, symbol, apiIndexes);
            }
        }

        foreach (var api in apiIndexes)
        {
            var symbol = await queries.GetSymbolAsync(api.IndexId, id, ct);
            if (symbol is not null)
            {
                return new ResolvedSymbolOutcome(authority, catalog, api, symbol, apiIndexes);
            }
        }

        return new ResolvedSymbolOutcome(authority, catalog, null, null, apiIndexes);
    }

    private static bool HasAnyIndex(ResolvedSymbolOutcome outcome) =>
        (outcome.Authority.Status == InstalledBuildAuthorityStatus.Resolved && outcome.Authority.IndexRun is not null)
        || outcome.ApiIndexes.Count > 0;

    private static string UnknownOrMissingPage(ResolvedSymbolOutcome outcome, string id) =>
        HasAnyIndex(outcome)
            ? Html.Layout("Unknown symbol", $"<h1>Unknown symbol</h1><p>No indexed symbol has ID '{Html.Escape(id)}'.</p>")
            : Html.Layout("No index", $"<h1>No index</h1><p>{Html.Escape(Html.AuthorityMessage(outcome.Authority))}</p>");

    private static string BadQueryPage(string message) =>
        Html.Layout("Invalid symbol query", $"<h1>Invalid symbol query</h1><p>{Html.Escape(message)}</p>");

    private static ToolEnvelope<SymbolQueryResult> MissingSymbolEnvelope(ResolvedSymbolOutcome outcome)
    {
        if (!HasAnyIndex(outcome))
        {
            return AuthorityEnvelope.From<SymbolQueryResult>(outcome.Authority);
        }

        if (outcome.Authority.Status == InstalledBuildAuthorityStatus.Resolved && outcome.Authority.IndexRun is not null)
        {
            return ServeEnvelopes.FromExactSymbol(outcome.Authority, null);
        }

        var selection = outcome.ApiIndexes[0].ApiSelection!;
        return ServeEnvelopes.FromApiExactSymbol(outcome.Catalog, selection, null);
    }

    private static ToolEnvelope<RelationshipQuerySetResult> MissingRelationshipsEnvelope(ResolvedSymbolOutcome outcome)
    {
        var notFound = new RelationshipQuerySetResult(
            new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []),
            [],
            null,
            false,
            string.Empty,
            null);
        if (!HasAnyIndex(outcome))
        {
            return AuthorityEnvelope.From<RelationshipQuerySetResult>(outcome.Authority);
        }

        if (outcome.Authority.Status == InstalledBuildAuthorityStatus.Resolved && outcome.Authority.IndexRun is not null)
        {
            return ServeEnvelopes.FromRelationships(outcome.Authority, notFound);
        }

        var selection = outcome.ApiIndexes[0].ApiSelection!;
        return ServeEnvelopes.FromApiRelationships(outcome.Catalog, selection, notFound);
    }
}
