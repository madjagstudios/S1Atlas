using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Web.Envelopes;

// Builds the /api/* envelopes with the same shapes, codes, messages, and
// provenance the matching MCP tools return.
internal static class ServeEnvelopes
{
    internal static ToolEnvelope<SymbolSearchResult> FromSearch(
        InstalledBuildAuthority authority,
        SymbolSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var build = BuildFrom(authority);
        if (result.TotalCount == 0)
        {
            return ToolEnvelope<SymbolSearchResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "index-search"));
        }

        return ToolEnvelope<SymbolSearchResult>.Resolved(
            build,
            result,
            Fact(authority, "index-search"),
            Derived(authority, "search-ranking"));
    }

    internal static ToolEnvelope<SymbolSearchResult> FromApiSearch(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        SymbolSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        if (result.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex)
        {
            return ToolEnvelope<SymbolSearchResult>.NotFound(
                build,
                new ToolError("NoCompletedIndex", "No completed API index exists for the requested scope."),
                provenance);
        }

        return result.TotalCount == 0
            ? ToolEnvelope<SymbolSearchResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed API symbol matched the selector."),
                provenance)
            : ToolEnvelope<SymbolSearchResult>.Resolved(build, result, provenance);
    }

    internal static ToolEnvelope<SymbolQueryResult> FromExactSymbol(
        InstalledBuildAuthority authority,
        SymbolQueryResult? symbol)
    {
        var build = BuildFrom(authority);
        return symbol is null
            ? ToolEnvelope<SymbolQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection"))
            : ToolEnvelope<SymbolQueryResult>.Resolved(
                build,
                symbol,
                Fact(authority, "index-symbol"),
                Derived(authority, "symbol-selection"));
    }

    internal static ToolEnvelope<SymbolQueryResult> FromApiExactSymbol(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        SymbolQueryResult? symbol)
    {
        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        return symbol is null
            ? ToolEnvelope<SymbolQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed API symbol matched the selector."),
                provenance)
            : ToolEnvelope<SymbolQueryResult>.Resolved(build, symbol, provenance);
    }

    internal static ToolEnvelope<RelationshipQuerySetResult> FromRelationships(
        InstalledBuildAuthority authority,
        RelationshipQuerySetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<RelationshipQuerySetResult>.Ambiguous(
                BuildFrom(authority),
                result.Resolution.Candidates.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NotFound => ToolEnvelope<RelationshipQuerySetResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<RelationshipQuerySetResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."),
                Derived(authority, "symbol-selection")),
            _ => ToolEnvelope<RelationshipQuerySetResult>.Resolved(
                BuildFrom(authority),
                result,
                Fact(authority, "relationship-query"),
                Derived(authority, "relationship-direction"))
        };
    }

    internal static ToolEnvelope<RelationshipQuerySetResult> FromApiRelationships(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        RelationshipQuerySetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<RelationshipQuerySetResult>.Ambiguous(
                build,
                result.Resolution.Candidates.Cast<object>().ToArray(),
                provenance),
            SymbolResolutionStatus.NotFound => ToolEnvelope<RelationshipQuerySetResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed API symbol matched the selector."),
                provenance),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<RelationshipQuerySetResult>.NotFound(
                build,
                new ToolError("NoCompletedIndex", "No completed API index exists for the requested scope."),
                provenance),
            _ => ToolEnvelope<RelationshipQuerySetResult>.Resolved(build, result, provenance)
        };
    }

    internal static ToolEnvelope<T> StoreMissing<T>() where T : class =>
        ToolEnvelope<T>.Unavailable(
            new ToolError("AtlasUnavailable", "The Atlas data store is unavailable."));

    internal static ToolEnvelope<T> FromApiSelectionFailure<T>(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection) where T : class
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);

        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        return selection.Availability switch
        {
            ApiIndexAvailability.Stale => ToolEnvelope<T>.Unavailable(
                new ToolError("StaleApiIndex", selection.Message ?? "The installed API index does not match the selected Schedule I build."),
                build,
                provenance),
            ApiIndexAvailability.Unavailable when selection.IndexId is null && IsMissingApiIndex(selection) => ToolEnvelope<T>.NotFound(
                build,
                new ToolError("NoCompletedIndex", selection.Message ?? "No completed API index exists for the requested scope."),
                provenance),
            ApiIndexAvailability.Unavailable => ToolEnvelope<T>.Unavailable(
                new ToolError("ApiIndexUnavailable", selection.Message ?? "The API index cannot be used with the selected authority."),
                build,
                provenance),
            ApiIndexAvailability.Ambiguous => ToolEnvelope<T>.Ambiguous(
                build,
                new object[] { selection },
                provenance),
            _ => throw new InvalidOperationException("A current API selection is not a failure.")
        };
    }

    private static bool IsMissingApiIndex(ApiIndexSelection selection) =>
        selection.Message?.StartsWith("No completed", StringComparison.Ordinal) == true;

    internal static BuildContext BuildFrom(InstalledBuildAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);

        return new BuildContext(
            authority.RequestedBuildId,
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId,
            "ScheduleI",
            "Installed",
            true);
    }

    private static ProvenanceEntry Fact(InstalledBuildAuthority authority, string source) =>
        new(
            ProvenanceClassification.Fact,
            source,
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId);

    private static ProvenanceEntry Derived(InstalledBuildAuthority authority, string source) =>
        new(
            ProvenanceClassification.Derived,
            source,
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId);

    private static BuildContext ApiBuildFrom(ApiIndexCatalogResult catalog, ApiIndexSelection selection) =>
        new(
            selection.Channel == CodeChannel.Installed ? catalog.RequestedBuildId : null,
            selection.Channel == CodeChannel.Installed ? catalog.ResolvedBuildId : null,
            ExtractionId: null,
            selection.IndexId,
            selection.Codebase.ToString(),
            selection.Channel.ToString(),
            selection.Availability == ApiIndexAvailability.Current);

    private static ProvenanceEntry[] ApiProvenance(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection)
    {
        var derived = new ProvenanceEntry(
            ProvenanceClassification.Derived,
            $"api-index-selection:{selection.Codebase}:{selection.Channel}",
            selection.Channel == CodeChannel.Installed ? catalog.ResolvedBuildId : null,
            ExtractionId: null,
            selection.IndexId);
        return selection.IndexId is null || selection.SourceIdentity is null
            ? [derived]
            : [ApiFact(selection, catalog.ResolvedBuildId), derived];
    }

    private static ProvenanceEntry ApiFact(ApiIndexSelection selection, string? resolvedBuildId) =>
        new(
            ProvenanceClassification.Fact,
            $"api-index:{selection.Codebase}:{selection.Channel}:source={selection.SourceIdentity ?? "unknown"}",
            selection.Channel == CodeChannel.Installed ? resolvedBuildId : null,
            ExtractionId: null,
            selection.IndexId);
}
