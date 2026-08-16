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
            return EnvelopeMapper.Invalid<SymbolDiff>(
                "InvalidArguments",
                "The selector and both build IDs must be provided.");
        }

        var authorityA = await _services.AuthorityResolver.ResolveAsync(buildIdA, ct);
        if (authorityA.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return AuthorityEnvelope.From<SymbolDiff>(authorityA);
        }

        var authorityB = await _services.AuthorityResolver.ResolveAsync(buildIdB, ct);
        if (authorityB.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return AuthorityEnvelope.From<SymbolDiff>(authorityB);
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
            return ToolEnvelope<SymbolDiff>.NotFound(
                EnvelopeMapper.BuildFrom(authorityA),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                CompareDerived(authorityA, "compare-symbol:left"),
                CompareDerived(authorityB, "compare-symbol:right"));
        }

        return ToolEnvelope<SymbolDiff>.Resolved(
            EnvelopeMapper.BuildFrom(authorityA),
            diff,
            CompareDerived(authorityA, "compare-symbol:left"),
            CompareDerived(authorityB, "compare-symbol:right"));
    }

    private static ProvenanceEntry CompareDerived(InstalledBuildAuthority authority, string source) =>
        new(
            ProvenanceClassification.Derived,
            source,
            authority.ResolvedBuildId,
            authority.ExtractionId,
            authority.IndexId);
}
