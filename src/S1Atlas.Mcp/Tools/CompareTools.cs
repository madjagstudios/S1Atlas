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
    public async Task<ComparisonToolEnvelope<SymbolDiff>> CompareSymbolAsync(
        [Description("Exact or fuzzy symbol selector to compare.")] string selector,
        [Description("Explicit build ID for the left-hand build.")] string? buildIdA,
        [Description("Explicit build ID for the right-hand build.")] string? buildIdB,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selector) ||
            string.IsNullOrWhiteSpace(buildIdA) ||
            string.IsNullOrWhiteSpace(buildIdB))
        {
            return ComparisonToolEnvelope<SymbolDiff>.Invalid(
                "InvalidArguments",
                "The selector and both build IDs must be provided.");
        }

        var authorityA = await _services.AuthorityResolver.ResolveAsync(buildIdA, ct);
        if (authorityA.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return ComparisonToolEnvelope<SymbolDiff>.FromAuthority(authorityA, null);
        }

        var authorityB = await _services.AuthorityResolver.ResolveAsync(buildIdB, ct);
        if (authorityB.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return ComparisonToolEnvelope<SymbolDiff>.FromAuthority(authorityB, authorityA);
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
            return ComparisonToolEnvelope<SymbolDiff>.NotFound(
                EnvelopeMapper.BuildFrom(authorityA),
                EnvelopeMapper.BuildFrom(authorityB),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                CompareDerived(authorityA, "compare-symbol:left"),
                CompareDerived(authorityB, "compare-symbol:right"));
        }

        return ComparisonToolEnvelope<SymbolDiff>.Resolved(
            EnvelopeMapper.BuildFrom(authorityA),
            EnvelopeMapper.BuildFrom(authorityB),
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

public sealed record ComparisonToolEnvelope<T>(
    ToolStatus Status,
    BuildContext? BuildA,
    BuildContext? BuildB,
    T? Data,
    IReadOnlyList<object> Candidates,
    IReadOnlyList<ProvenanceEntry> Provenance,
    ToolError? Error) where T : class
{
    public BuildContext? Build => BuildA;

    public static ComparisonToolEnvelope<T> Invalid(string code, string message) =>
        new(ToolStatus.Invalid, null, null, null, Array.Empty<object>(), Array.Empty<ProvenanceEntry>(), new(code, message));

    public static ComparisonToolEnvelope<T> FromAuthority(
        InstalledBuildAuthority authority,
        InstalledBuildAuthority? resolvedA)
    {
        var standard = AuthorityEnvelope.From<T>(authority);
        return new(
            standard.Status,
            resolvedA is null ? Context(authority) : EnvelopeMapper.BuildFrom(resolvedA),
            Context(authority),
            standard.Data,
            standard.Candidates,
            standard.Provenance,
            standard.Error);
    }

    public static ComparisonToolEnvelope<T> NotFound(
        BuildContext buildA,
        BuildContext buildB,
        ToolError error,
        params ProvenanceEntry[] provenance) =>
        new(ToolStatus.NotFound, buildA, buildB, null, Array.Empty<object>(), provenance, error);

    public static ComparisonToolEnvelope<T> Resolved(
        BuildContext buildA,
        BuildContext buildB,
        T data,
        params ProvenanceEntry[] provenance) =>
        new(ToolStatus.Resolved, buildA, buildB, data, Array.Empty<object>(), provenance, null);

    private static BuildContext? Context(InstalledBuildAuthority authority) =>
        authority.RequestedBuildId is null && authority.ResolvedBuildId is null
            ? null
            : new BuildContext(
                authority.RequestedBuildId,
                authority.ResolvedBuildId,
                authority.ExtractionId,
                authority.IndexId,
                "ScheduleI",
                "Installed",
                authority.Status == InstalledBuildAuthorityStatus.Resolved);
}
