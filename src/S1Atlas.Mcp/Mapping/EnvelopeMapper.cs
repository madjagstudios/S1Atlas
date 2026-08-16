using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Mcp.Mapping;

public static class EnvelopeMapper
{
    public static async Task<ToolEnvelope<T>> WithAuthorityAsync<T>(
        InstalledBuildAuthorityResolver resolver,
        string? buildId,
        CancellationToken ct,
        Func<InstalledBuildAuthority, Task<ToolEnvelope<T>>> onResolved) where T : class
    {
        var authority = await resolver.ResolveAsync(buildId, ct);
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return AuthorityEnvelope.From<T>(authority);
        }

        return await onResolved(authority);
    }

    public static BuildContext BuildFrom(InstalledBuildAuthority authority)
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

    public static ToolEnvelope<SymbolSearchResult> FromSearch(
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

    public static ToolEnvelope<SymbolQueryResult> FromFind(
        InstalledBuildAuthority authority,
        IReadOnlyList<SymbolQueryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var build = BuildFrom(authority);
        return results.Count switch
        {
            0 => ToolEnvelope<SymbolQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            1 => ToolEnvelope<SymbolQueryResult>.Resolved(
                build,
                results[0],
                Fact(authority, "index-symbol"),
                Derived(authority, "symbol-selection")),
            _ => ToolEnvelope<SymbolQueryResult>.Ambiguous(
                build,
                results.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection"))
        };
    }

    public static ToolEnvelope<SourceSnippetQueryResult> FromSource(
        InstalledBuildAuthority authority,
        SourceSnippetResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<SourceSnippetQueryResult>.Ambiguous(
                BuildFrom(authority),
                result.Resolution.Candidates.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NotFound => ToolEnvelope<SourceSnippetQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<SourceSnippetQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."),
                Derived(authority, "symbol-selection")),
            _ when result.Snippet is null => ToolEnvelope<SourceSnippetQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SourceUnavailable", "The selected symbol has no indexed source location."),
                Derived(authority, "source-selection")),
            _ => ToolEnvelope<SourceSnippetQueryResult>.Resolved(
                BuildFrom(authority),
                result.Snippet,
                Fact(authority, "source-snippet"),
                Derived(authority, "source-selection"))
        };
    }

    public static ToolEnvelope<RelationshipQuerySetResult> FromRelationships(
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

    public static ToolEnvelope<T> Invalid<T>(string code, string message) where T : class =>
        ToolEnvelope<T>.Invalid(new ToolError(code, message));

    public static ToolEnvelope<T> SourceIntegrityFailure<T>(
        InstalledBuildAuthority authority,
        string message) where T : class =>
        ToolEnvelope<T>.Unavailable(
            new ToolError("SourceIntegrityFailure", message),
            BuildFrom(authority),
            Derived(authority, "source-integrity"));

    public static ToolEnvelope<T> SourceUnavailable<T>(
        InstalledBuildAuthority authority,
        string message) where T : class =>
        ToolEnvelope<T>.Unavailable(
            new ToolError("SourceUnavailable", message),
            BuildFrom(authority),
            Derived(authority, "source-selection"));

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
}
