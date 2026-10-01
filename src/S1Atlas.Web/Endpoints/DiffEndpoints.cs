using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Web.Api;
using S1Atlas.Web.Envelopes;
using S1Atlas.Web.Queries;
using S1Atlas.Web.Rendering;

namespace S1Atlas.Web.Endpoints;

internal static class DiffEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/diff", ["GET", "HEAD"], (HttpContext c, ServeQueries q, CancellationToken ct) => DiffPageAsync(c, q, ct));
        app.MapMethods("/api/diff", ["GET", "HEAD"], (HttpContext c, ServeQueries q, CancellationToken ct) => ApiDiffAsync(c, q, ct));
    }

    private static async Task<IResult> DiffPageAsync(
        HttpContext context,
        ServeQueries queries,
        CancellationToken ct)
    {
        DiffArgs args;
        try
        {
            args = QueryBinding.BindDiff(context.Request.Query);
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Html(BadQueryPage(exception.Message), StatusCodes.Status400BadRequest);
        }

        if (args.Codebase != CodebaseKind.ScheduleI)
        {
            return ServeHttp.Html(
                BadQueryPage("Diffing is only supported for the schedule-i codebase."),
                StatusCodes.Status400BadRequest);
        }

        if (args.From is null || args.To is null)
        {
            try
            {
                var builds = await queries.ListBuildsAsync(ct);
                return ServeHttp.Html(DiffView.RenderPicker(builds, args.From, args.To, args.Kind));
            }
            catch (AtlasStoreMissingException)
            {
                return ServeHttp.Html(Html.Layout(
                    "Diff",
                    "<h1>Diff</h1><p>No Atlas data store was found.</p>"));
            }
        }

        if (args.From.Equals(args.To, StringComparison.Ordinal))
        {
            return ServeHttp.Html(
                BadQueryPage("Both build identifiers resolve to the same build. Provide two different builds."),
                StatusCodes.Status400BadRequest);
        }

        try
        {
            var outcome = await ResolveAsync(queries, args.From, args.To, args.Kind, args.Page, ct);
            if (outcome.Error is not null)
            {
                return ServeHttp.Html(
                    Html.Layout("Diff", $"<h1>Diff</h1><p>{Html.Escape(outcome.Error)}</p>"),
                    outcome.Status);
            }

            return ServeHttp.Html(DiffView.Render(new DiffModel(outcome.Result!, args.Kind)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(Html.Layout(
                "Diff",
                "<h1>Diff</h1><p>No Atlas data store was found.</p>"));
        }
    }

    private static async Task<IResult> ApiDiffAsync(
        HttpContext context,
        ServeQueries queries,
        CancellationToken ct)
    {
        DiffArgs args;
        try
        {
            args = QueryBinding.BindDiff(context.Request.Query);
        }
        catch (ServeInvalidQueryException exception)
        {
            return ServeHttp.Envelope(ToolEnvelope<ServeDiffResult>.Invalid(
                new ToolError("InvalidQuery", exception.Message)));
        }

        if (args.Codebase != CodebaseKind.ScheduleI)
        {
            return ServeHttp.Envelope(ToolEnvelope<ServeDiffResult>.Invalid(
                new ToolError("UnsupportedCodebase", "Diffing is only supported for the schedule-i codebase.")));
        }

        if (args.From is null || args.To is null)
        {
            return ServeHttp.Envelope(ToolEnvelope<ServeDiffResult>.Invalid(
                new ToolError("InvalidQuery", "Diff requires both ?from= and ?to= build IDs.")));
        }

        if (args.From.Equals(args.To, StringComparison.Ordinal))
        {
            return ServeHttp.Envelope(ToolEnvelope<ServeDiffResult>.Invalid(
                new ToolError("SameBuild", "Both build identifiers resolve to the same build. Provide two different builds.")));
        }

        try
        {
            var outcome = await ResolveAsync(queries, args.From, args.To, args.Kind, args.Page, ct);
            if (outcome.Envelope is not null)
            {
                return ServeHttp.Envelope(outcome.Envelope);
            }

            var result = outcome.Result!;
            return ServeHttp.Envelope(ToolEnvelope<ServeDiffResult>.Resolved(
                ServeEnvelopes.BuildFrom(outcome.ToAuthority!),
                result,
                new ProvenanceEntry(
                    ProvenanceClassification.Derived,
                    "build-diff",
                    result.ToBuildId,
                    outcome.ToAuthority!.ExtractionId,
                    outcome.ToAuthority!.IndexId)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<ServeDiffResult>());
        }
    }

    private sealed record DiffOutcome(
        ServeDiffResult? Result,
        InstalledBuildAuthority? ToAuthority,
        string? Error,
        int Status,
        ToolEnvelope<ServeDiffResult>? Envelope);

    private static async Task<DiffOutcome> ResolveAsync(
        ServeQueries queries,
        string from,
        string to,
        SymbolKind? kind,
        int page,
        CancellationToken ct)
    {
        var history = await queries.GetHistoryAsync(ct);
        foreach (var buildId in new[] { from, to })
        {
            if (!history.Entries.Any(entry =>
                    entry.Build.BuildId.Equals(buildId, StringComparison.Ordinal)))
            {
                return new DiffOutcome(
                    null,
                    null,
                    $"No indexed build has ID '{buildId}'.",
                    StatusCodes.Status404NotFound,
                    ToolEnvelope<ServeDiffResult>.NotFound(
                        new BuildContext(buildId, null, null, null, "ScheduleI", "Installed", false),
                        new ToolError("BuildNotFound", "The requested build was not found."),
                        new ProvenanceEntry(
                            ProvenanceClassification.Derived, "build-selection", buildId, null, null)));
            }
        }

        var fromAuthority = await queries.ResolveAuthorityAsync(ct, from);
        var toAuthority = await queries.ResolveAuthorityAsync(ct, to);
        foreach (var authority in new[] { fromAuthority, toAuthority })
        {
            if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
            {
                return new DiffOutcome(
                    null,
                    null,
                    authority.Message ?? "The requested build is not indexed.",
                    authority.Status == InstalledBuildAuthorityStatus.ExtractionIntegrityFailure
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status404NotFound,
                    AuthorityEnvelope.From<ServeDiffResult>(authority));
            }
        }

        var indexIdA = fromAuthority.IndexId!;
        var indexIdB = toAuthority.IndexId!;
        if (indexIdA.Equals(indexIdB, StringComparison.Ordinal))
        {
            const string message = "Both build identifiers resolve to the same index. Provide two different builds.";
            return new DiffOutcome(
                null,
                null,
                message,
                StatusCodes.Status400BadRequest,
                ToolEnvelope<ServeDiffResult>.Invalid(new ToolError("SameIndex", message)));
        }

        var diff = await queries.DiffAsync(indexIdA, indexIdB, kind?.ToString(), ct);
        var lastPage = diff.Changes.Count == 0
            ? 0
            : (diff.Changes.Count - 1) / QueryBinding.DiffPageSize;
        if (page > lastPage)
        {
            var message = $"Page {page} is beyond the last page ({lastPage}) for this diff.";
            return new DiffOutcome(
                null,
                null,
                message,
                StatusCodes.Status400BadRequest,
                ToolEnvelope<ServeDiffResult>.Invalid(new ToolError("InvalidPage", message)));
        }

        var current = await queries.ResolveAuthorityAsync(ct);
        var currentRun = current.Status == InstalledBuildAuthorityStatus.Resolved
            ? current.IndexRun
            : null;
        var toRun = toAuthority.IndexRun!;
        var pageChanges = diff.Changes
            .Skip(page * QueryBinding.DiffPageSize)
            .Take(QueryBinding.DiffPageSize)
            .ToArray();
        var changes = new List<ServeDiffChange>(pageChanges.Length);
        foreach (var change in pageChanges)
        {
            changes.Add(new ServeDiffChange(
                change.CanonicalKey,
                change.QualifiedName,
                change.Kind,
                change.Classification,
                change.SignatureBefore,
                change.SignatureAfter,
                await ResolveSymbolIdAsync(queries, toRun, currentRun, change.CanonicalKey, ct)));
        }

        var result = new ServeDiffResult(
            from,
            to,
            indexIdA,
            indexIdB,
            diff.Codebase,
            diff.Channel,
            diff.TotalSymbolsA,
            diff.TotalSymbolsB,
            new ServeDiffCounts(
                diff.CountsByClassification.GetValueOrDefault(DiffClassification.Added),
                diff.CountsByClassification.GetValueOrDefault(DiffClassification.Removed),
                diff.CountsByClassification.GetValueOrDefault(DiffClassification.MethodBodyChanged),
                diff.CountsByClassification.GetValueOrDefault(DiffClassification.RelationshipsChanged),
                diff.CountsByClassification.GetValueOrDefault(DiffClassification.Unchanged)),
            diff.Changes.Count,
            page,
            QueryBinding.DiffPageSize,
            changes);
        return new DiffOutcome(result, toAuthority, null, StatusCodes.Status200OK, null);
    }

    private static async Task<string?> ResolveSymbolIdAsync(
        ServeQueries queries,
        IndexRunRecord toRun,
        IndexRunRecord? currentRun,
        string canonicalKey,
        CancellationToken ct)
    {
        var inTo = await queries.GetCanonicalSymbolsAsync(toRun, canonicalKey, ct);
        if (inTo.Count == 0 || currentRun is null)
        {
            return null;
        }

        if (toRun.IndexId.Equals(currentRun.IndexId, StringComparison.Ordinal))
        {
            return inTo[0].SymbolId;
        }

        var inCurrent = await queries.GetCanonicalSymbolsAsync(currentRun, canonicalKey, ct);
        return inCurrent.Count == 0 ? null : inCurrent[0].SymbolId;
    }

    private static string BadQueryPage(string message) =>
        Html.Layout("Diff", $"<h1>Diff</h1><p>{Html.Escape(message)}</p>");
}
