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

internal static class SearchEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/search", ["GET", "HEAD"], SearchPageAsync);
        app.MapMethods("/api/search", ["GET", "HEAD"], ApiSearchAsync);
    }

    private static async Task<IResult> SearchPageAsync(
        HttpContext context,
        ServeQueries queries,
        CancellationToken ct)
    {
        SearchArgs args;
        try
        {
            args = QueryBinding.BindSearch(context.Request.Query);
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Html(BadQueryPage(exception.Message), StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return ServeHttp.Html(SearchPromptPage(args));
        }

        SymbolSearchResult result;
        string channel;
        try
        {
            (result, channel) = await RunSearchAsync(queries, args, ct);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(SearchUnavailablePage(args, "No Atlas data store was found."));
        }
        catch (ServeScopeMissingException exception)
        {
            return ServeHttp.Html(SearchUnavailablePage(args, exception.Message));
        }

        var page = result.Results
            .Skip(args.Page * QueryBinding.SearchPageSize)
            .Take(QueryBinding.SearchPageSize)
            .ToArray();
        var model = new SearchModel(
            args.Query,
            args.Kind?.ToString(),
            args.Codebase,
            channel,
            args.Page,
            QueryBinding.SearchPageSize,
            result with { Results = page, ReturnedCount = page.Length });
        return ServeHttp.Html(SearchView.Render(model));
    }

    private static async Task<IResult> ApiSearchAsync(
        HttpContext context,
        ServeQueries queries,
        CancellationToken ct)
    {
        SearchArgs args;
        try
        {
            args = QueryBinding.BindSearch(context.Request.Query);
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Envelope(ToolEnvelope<SymbolSearchResult>.Invalid(
                new ToolError("InvalidQuery", exception.Message)));
        }

        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return ServeHttp.Envelope(ToolEnvelope<SymbolSearchResult>.Invalid(
                new ToolError("InvalidQuery", "The q parameter is required.")));
        }

        try
        {
            var envelope = await RunApiSearchAsync(queries, args, ct);
            return ServeHttp.Envelope(envelope);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<SymbolSearchResult>());
        }
    }

    private static async Task<(SymbolSearchResult Result, string Channel)> RunSearchAsync(
        ServeQueries queries,
        SearchArgs args,
        CancellationToken ct)
    {
        var limit = (args.Page + 1) * QueryBinding.SearchPageSize;
        if (args.Codebase == CodebaseKind.ScheduleI)
        {
            var authority = await queries.ResolveAuthorityAsync(ct);
            if (authority.Status != InstalledBuildAuthorityStatus.Resolved || authority.IndexRun is null)
            {
                throw new ServeScopeMissingException(Html.AuthorityMessage(authority));
            }

            return (
                await queries.SearchGameAsync(authority.IndexRun, args.Query, args.Kind, limit, ct),
                Html.ChannelLabel(CodeChannel.Installed));
        }

        if (args.Kind is not null)
        {
            throw new ServeInvalidQueryException("Kind filtering is only supported with codebase=schedule-i.");
        }

        var catalog = await queries.ListApiCatalogAsync(ct);
        var selection = catalog.Selections.FirstOrDefault(candidate =>
            candidate.Codebase == args.Codebase
            && candidate.Availability == ApiIndexAvailability.Current
            && candidate.IndexId is not null)
            ?? throw new ServeScopeMissingException(
                $"No completed {Html.CodebaseLabel(args.Codebase)} index exists yet.");
        return (
            await queries.SearchApiAsync(selection, args.Query, limit, ct),
            Html.ChannelLabel(selection.Channel));
    }

    private static async Task<ToolEnvelope<SymbolSearchResult>> RunApiSearchAsync(
        ServeQueries queries,
        SearchArgs args,
        CancellationToken ct)
    {
        var limit = (args.Page + 1) * QueryBinding.SearchPageSize;
        if (args.Codebase == CodebaseKind.ScheduleI)
        {
            var authority = await queries.ResolveAuthorityAsync(ct);
            if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
            {
                return AuthorityEnvelope.From<SymbolSearchResult>(authority);
            }

            if (authority.IndexRun is null)
            {
                return ToolEnvelope<SymbolSearchResult>.NotFound(
                    ServeEnvelopes.BuildFrom(authority),
                    new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."));
            }

            var result = await queries.SearchGameAsync(authority.IndexRun, args.Query, args.Kind, limit, ct);
            return ServeEnvelopes.FromSearch(authority, Slice(result, args));
        }

        if (args.Kind is not null)
        {
            return ToolEnvelope<SymbolSearchResult>.Invalid(
                new ToolError("InvalidQuery", "Kind filtering is only supported with codebase=schedule-i."));
        }

        var catalog = await queries.ListApiCatalogAsync(ct);
        var selection = catalog.Selections.FirstOrDefault(candidate =>
            candidate.Codebase == args.Codebase
            && candidate.Availability == ApiIndexAvailability.Current
            && candidate.IndexId is not null);
        if (selection is null)
        {
            var fallback = catalog.Selections.FirstOrDefault(candidate => candidate.Codebase == args.Codebase)
                ?? new ApiIndexSelection(args.Codebase, CodeChannel.Release, ApiIndexAvailability.Unavailable, null, null, null, null, null);
            return ServeEnvelopes.FromApiSelectionFailure<SymbolSearchResult>(catalog, fallback);
        }

        var apiResult = await queries.SearchApiAsync(selection, args.Query, limit, ct);
        return ServeEnvelopes.FromApiSearch(catalog, selection, Slice(apiResult, args));
    }

    private static SymbolSearchResult Slice(SymbolSearchResult result, SearchArgs args)
    {
        var page = result.Results
            .Skip(args.Page * QueryBinding.SearchPageSize)
            .Take(QueryBinding.SearchPageSize)
            .ToArray();
        return result with { Results = page, ReturnedCount = page.Length };
    }

    private static string BadQueryPage(string message) =>
        Html.Layout("Invalid search", $"<h1>Invalid search</h1><p>{Html.Escape(message)}</p>");

    private static string SearchPromptPage(SearchArgs args) =>
        Html.Layout(
            "Search",
            "<h1>Search</h1>" + SearchFormOnly(args) + "<p>Enter a query to search the index.</p>");

    private static string SearchUnavailablePage(SearchArgs args, string message) =>
        Html.Layout(
            "Search",
            "<h1>Search</h1>" + SearchFormOnly(args) + $"<p>{Html.Escape(message)}</p>");

    private static string SearchFormOnly(SearchArgs args) =>
        SearchView.RenderForm(new SearchModel(args.Query, args.Kind?.ToString(), args.Codebase, string.Empty, args.Page, QueryBinding.SearchPageSize, new SymbolSearchResult(0, 0, [], null)));
}
