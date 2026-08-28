using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

/// <summary>
/// Shared selector-normalization and target-query logic for call-site and field-reference
/// queries. Both <see cref="IndexQueryService"/> and <see cref="FederatedIndexQueryService"/>
/// resolve the same selectors against the same relationship target text, so the rules live in
/// one place to keep the game-scope and reference/all-scope paths from drifting.
/// </summary>
internal static class CallSiteSelectors
{
    internal static IReadOnlyList<string> FieldRelationshipKinds(FieldReferenceFilter filter) => filter switch
    {
        FieldReferenceFilter.All => ["ReadsField", "WritesField"],
        FieldReferenceFilter.Readers => ["ReadsField"],
        FieldReferenceFilter.Writers => ["WritesField"],
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    internal static async Task<CallSiteTargetQuery> ResolveTargetQueryAsync(
        SymbolResolver resolver,
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(run.IndexId, selector, codebase, channel, cancellationToken);
        if (resolution.Status == SymbolResolutionStatus.Resolved && resolution.Symbol is not null)
        {
            var resolvedTarget = TargetTextFromResolvedSymbol(resolution.Symbol);
            return HasExplicitCallSiteParameters(selector)
                ? new CallSiteTargetQuery(resolvedTarget, RelationshipTargetTextMatchMode.Prefix)
                : new CallSiteTargetQuery(StripParameterList(resolvedTarget), RelationshipTargetTextMatchMode.Prefix);
        }

        var normalized = NormalizeCallSiteSelector(selector);
        return HasExplicitCallSiteParameters(normalized)
            ? new CallSiteTargetQuery(normalized, RelationshipTargetTextMatchMode.Prefix)
            : new CallSiteTargetQuery(StripParameterList(normalized), RelationshipTargetTextMatchMode.Prefix);
    }

    private static bool HasExplicitCallSiteParameters(string selector) =>
        selector.IndexOf('(') >= 0;

    private static string StripParameterList(string selector)
    {
        var separator = selector.IndexOf('(');
        return separator >= 0 ? selector[..separator] : selector;
    }

    private static string NormalizeCallSiteSelector(string selector)
    {
        var trimmed = selector.Trim();
        var parameterSeparator = trimmed.IndexOf('(');
        var head = parameterSeparator >= 0 ? trimmed[..parameterSeparator] : trimmed;
        if (head.Contains("::", StringComparison.Ordinal))
            return trimmed;

        var lastDot = head.LastIndexOf('.');
        if (lastDot < 0)
            return trimmed;

        var normalizedHead = head[..lastDot] + "::" + head[(lastDot + 1)..];
        return parameterSeparator >= 0
            ? normalizedHead + trimmed[parameterSeparator..]
            : normalizedHead;
    }

    private static string TargetTextFromResolvedSymbol(SymbolQueryResult symbol)
    {
        var signature = symbol.Signature;
        var memberSeparator = signature.IndexOf("::", StringComparison.Ordinal);
        if (memberSeparator <= 0)
            return signature;

        var returnSeparator = signature.LastIndexOf(' ', memberSeparator);
        return returnSeparator >= 0 && returnSeparator + 1 < signature.Length
            ? signature[(returnSeparator + 1)..]
            : signature;
    }
}

internal readonly record struct CallSiteTargetQuery(
    string TargetText,
    RelationshipTargetTextMatchMode MatchMode);
