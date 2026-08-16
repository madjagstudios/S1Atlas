using System.ComponentModel;
using ModelContextProtocol.Server;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Diff;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class CompareTools
{
    private readonly McpReadOnlyServices _services;

    public CompareTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "compare_symbol"), Description("Compare one installed Schedule I symbol across two explicit builds.")]
    public async Task<ToolEnvelope<SymbolDiff>> CompareSymbolAsync(
        [Description("Exact or fuzzy symbol selector to compare.")] string selector,
        [Description("Explicit build ID for the left-hand build.")] string? buildIdA,
        [Description("Explicit build ID for the right-hand build.")] string? buildIdB,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selector) ||
            string.IsNullOrWhiteSpace(buildIdA) ||
            string.IsNullOrWhiteSpace(buildIdB))
        {
            return ToolEnvelope<SymbolDiff>.Invalid(
                new ToolError("InvalidArguments", "The selector and both build IDs must be provided."));
        }

        var authorityA = await _services.AuthorityResolver.ResolveAsync(buildIdA, ct);
        if (authorityA.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return WithContexts(AuthorityEnvelope.From<SymbolDiff>(authorityA), Context(authorityA), MissingContext(buildIdB));
        }

        var authorityB = await _services.AuthorityResolver.ResolveAsync(buildIdB, ct);
        if (authorityB.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            var failure = AuthorityEnvelope.From<SymbolDiff>(authorityB);
            failure = failure with
            {
                Provenance = new[] { AuthorityFact(authorityA, "installed-build-authority:left") }
                    .Concat(failure.Provenance)
                    .ToArray()
            };
            return WithContexts(failure, EnvelopeMapper.BuildFrom(authorityA), Context(authorityB));
        }

        var diff = await _services.BuildDiffService.DiffSymbolAsync(
            authorityA.IndexId!,
            authorityB.IndexId!,
            "ScheduleI",
            "Installed",
            selector,
            ct);

        if (diff is null)
        {
            return WithContexts(ToolEnvelope<SymbolDiff>.NotFound(
                EnvelopeMapper.BuildFrom(authorityA),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                AuthorityFact(authorityA, "installed-build-authority:left"),
                AuthorityFact(authorityB, "installed-build-authority:right"),
                CompareDerived(authorityA, "compare-symbol:left"),
                CompareDerived(authorityB, "compare-symbol:right")),
                EnvelopeMapper.BuildFrom(authorityA), EnvelopeMapper.BuildFrom(authorityB));
        }

        return WithContexts(ToolEnvelope<SymbolDiff>.Resolved(
            EnvelopeMapper.BuildFrom(authorityA),
            diff,
            AuthorityFact(authorityA, "installed-build-authority:left"),
            AuthorityFact(authorityB, "installed-build-authority:right"),
            CompareDerived(authorityA, "compare-symbol:left"),
            CompareDerived(authorityB, "compare-symbol:right")),
            EnvelopeMapper.BuildFrom(authorityA), EnvelopeMapper.BuildFrom(authorityB));
    }

    private static ToolEnvelope<SymbolDiff> WithContexts(
        ToolEnvelope<SymbolDiff> envelope,
        BuildContext? buildA,
        BuildContext? buildB) => envelope with { BuildA = buildA, BuildB = buildB };

    private static BuildContext? Context(InstalledBuildAuthority authority) =>
        authority.RequestedBuildId is null && authority.ResolvedBuildId is null
            ? null
            : new BuildContext(authority.RequestedBuildId, authority.ResolvedBuildId, authority.ExtractionId,
                authority.IndexId, "ScheduleI", "Installed",
                authority.Status == InstalledBuildAuthorityStatus.Resolved);

    private static BuildContext MissingContext(string requestedBuildId) =>
        new(requestedBuildId, null, null, null, "ScheduleI", "Installed", false);

    private static ProvenanceEntry AuthorityFact(InstalledBuildAuthority authority, string source) =>
        new(ProvenanceClassification.Fact, source, authority.ResolvedBuildId ?? authority.RequestedBuildId,
            authority.ExtractionId, authority.IndexId);

    private static ProvenanceEntry CompareDerived(InstalledBuildAuthority authority, string source) =>
        new(
            ProvenanceClassification.Derived,
            source,
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId);
}
