using System.ComponentModel;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class SeamTools
{
    private readonly McpReadOnlyServices _services;

    public SeamTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "investigate_seam"), Description("Investigate whether a resolved symbol is a supportable ownership seam.")]
    public async Task<ToolEnvelope<SeamInvestigationResult>> InvestigateSeamAsync(
        [Description("The behavioral question that frames the seam investigation.")] string behavioralQuestion,
        [Description("Exact or fuzzy symbol selector for the seam under investigation.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null,
        [Description("Maximum relationship evidence rows to inspect (1-50).")] int relationshipLimit = 50,
        [Description("Maximum owner candidates to return (1-50).")] int ownerLimit = 10,
        [Description("Lines of source context to include around the selected seam.")] int context = 5,
        [Description("Preserve detailed claims and evidence sections in the result payload.")] bool details = false,
        CancellationToken ct = default,
        [Description("Optional native symbol IDs for an explicitly requested, read-only native evidence lookup.")] IReadOnlyList<string>? nativeSymbolIds = null,
        [Description("Native traversal budget (0 disables native recovery; maximum 500 edges).")] int nativeTraversalBudget = 0)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateQuestion(behavioralQuestion, authority, out ToolEnvelope<SeamInvestigationResult> questionError))
                    return questionError;

                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<SeamInvestigationResult> selectorError))
                    return selectorError;

                if (!ToolArguments.TryBoundRelationshipLimit(
                        relationshipLimit,
                        authority,
                        out var boundedRelationshipLimit,
                        out ToolEnvelope<SeamInvestigationResult> relationshipError))
                {
                    return relationshipError;
                }

                if (!ToolArguments.TryBoundOwnerLimit(
                        ownerLimit,
                        authority,
                        out var boundedOwnerLimit,
                        out ToolEnvelope<SeamInvestigationResult> ownerError))
                {
                    return ownerError;
                }

                if (!ToolArguments.TryBoundContext(
                        context,
                        authority,
                        out var boundedContext,
                        out ToolEnvelope<SeamInvestigationResult> contextError))
                {
                    return contextError;
                }

                if (!ToolArguments.TryBoundNativeBudget(
                        nativeTraversalBudget,
                        authority,
                        out var boundedNativeBudget,
                        out ToolEnvelope<SeamInvestigationResult> nativeBudgetError))
                {
                    return nativeBudgetError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<SeamInvestigationResult> scopeError))
                {
                    return scopeError;
                }

                var pinned = await PinAuthorityAsync<SeamInvestigationResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority with { RequestedBuildId = buildId };
                options = options with { Limit = boundedRelationshipLimit };

                var request = new SeamInvestigationRequest(
                    behavioralQuestion.Trim(),
                    selector.Trim(),
                    options,
                    boundedRelationshipLimit,
                    boundedOwnerLimit,
                    boundedContext,
                    details,
                    nativeSymbolIds,
                    boundedNativeBudget);
                var investigationService = CreatePinnedService(
                    authority,
                    options.ReferenceCollection,
                    pinned.ReferenceCollection?.ReferenceIndexId,
                    ct);
                var result = await investigationService.InvestigateAsync(request, ct);
                return EnvelopeMapper.FromScopedSeamInvestigation(
                    authority,
                    result,
                    options.ReferenceCollection,
                    pinned.ReferenceCollection,
                    options.Scope);
            });
    }

    private SeamInvestigationService CreatePinnedService(
        S1Atlas.Application.Authority.InstalledBuildAuthority authority,
        string? referenceCollection,
        string? referenceIndexId,
        CancellationToken cancellationToken)
    {
        var pinnedRepository = new PinnedQueryAuthorityRepository(
            _services.Repository,
            authority.IndexRun,
            referenceCollection,
            referenceIndexId,
            cancellationToken);
        return new SeamInvestigationService(
            new IndexQueryService(pinnedRepository, _services.DataRoot),
            new FederatedIndexQueryService(pinnedRepository, _services.DataRoot),
            new ReferenceModQueryService(pinnedRepository, _services.DataRoot),
            _services.Repository,
            _services.Repository);
    }

    private async Task<ScopedAuthority<T>> PinAuthorityAsync<T>(
        S1Atlas.Application.Authority.InstalledBuildAuthority authority,
        string? requestedBuildId,
        IndexQueryOptions options,
        CancellationToken ct) where T : class
    {
        if (options.Scope == IndexQueryScope.Game)
            return new(authority, null);

        var resolvedCollection = await _services.ReferenceModQueryService.GetCollectionAuthorityAsync(
            options.ReferenceCollection!,
            ct);
        if (resolvedCollection is null)
        {
            return new(
                authority,
                ToolEnvelope<T>.NotFound(
                    EnvelopeMapper.BuildFrom(authority),
                    new ToolError("NoCompletedIndex", "No completed reference collection exists for the requested scope."),
                    new ProvenanceEntry(ProvenanceClassification.Derived, "index-selection", authority.ResolvedBuildId, authority.ExtractionId, authority.IndexId)));
        }

        var baseAuthority = await _services.AuthorityResolver.ResolveAsync(resolvedCollection.BuildId, ct);
        if (baseAuthority.Status != S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.Resolved)
            return new(baseAuthority, S1Atlas.Application.Envelope.AuthorityEnvelope.From<T>(baseAuthority));

        if (!string.IsNullOrWhiteSpace(requestedBuildId) &&
            !string.Equals(requestedBuildId, resolvedCollection.BuildId, StringComparison.Ordinal))
        {
            return new(
                baseAuthority,
                ToolEnvelope<T>.Invalid(
                    new ToolError(
                        "ReferenceCollectionBuildMismatch",
                        "The requested build does not match the reference collection's recorded base build."),
                    EnvelopeMapper.BuildFrom(baseAuthority),
                    new ProvenanceEntry(
                        ProvenanceClassification.Fact,
                        "reference-collection-base",
                        resolvedCollection.BuildId,
                        baseAuthority.ExtractionId,
                        resolvedCollection.BaseIndexId)));
        }

        if (!string.Equals(baseAuthority.IndexId, resolvedCollection.BaseIndexId, StringComparison.Ordinal))
        {
            return new(
                baseAuthority,
                ToolEnvelope<T>.Invalid(
                    new ToolError(
                        "ReferenceCollectionBaseIndexMismatch",
                        "The reference collection's recorded base index is not the authoritative index for its build."),
                    EnvelopeMapper.BuildFrom(baseAuthority),
                    new ProvenanceEntry(
                        ProvenanceClassification.Fact,
                        "reference-collection-base",
                        resolvedCollection.BuildId,
                        baseAuthority.ExtractionId,
                        resolvedCollection.BaseIndexId)));
        }

        return new(baseAuthority, null, resolvedCollection);
    }

    private sealed record ScopedAuthority<T>(
        S1Atlas.Application.Authority.InstalledBuildAuthority Authority,
        ToolEnvelope<T>? Error,
        ReferenceCollectionAuthorityQueryResult? ReferenceCollection = null) where T : class;

    private sealed class PinnedQueryAuthorityRepository : IIndexRepository
    {
        private readonly IIndexRepository _inner;
        private readonly IndexRunRecord? _installedPinnedRun;
        private readonly string? _referenceCollection;
        private readonly IndexRunRecord? _referencePinnedRun;
        private readonly IndexRunRecord? _referenceGamePinnedRun;

        public PinnedQueryAuthorityRepository(
            IIndexRepository inner,
            IndexRunRecord? installedPinnedRun,
            string? referenceCollection,
            string? referenceIndexId,
            CancellationToken cancellationToken)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _installedPinnedRun = installedPinnedRun;
            _referenceCollection = referenceCollection;

            if (referenceIndexId is null)
                return;

            _referencePinnedRun = _inner.GetCompletedIndexAsync(referenceIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority index '{referenceIndexId}' is unavailable.");
            var context = _inner.GetReferenceIndexContextAsync(referenceIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority '{referenceIndexId}' has no persisted base game context.");
            _referenceGamePinnedRun = _inner.GetCompletedIndexAsync(context.GameIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority base game index '{context.GameIndexId}' is unavailable.");
        }

        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) =>
            _inner.CreateCodeSnapshotAsync(snapshot, cancellationToken);

        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) =>
            _inner.GetCodeSnapshotAsync(snapshotId, cancellationToken);

        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) =>
            _inner.StartIndexRunAsync(run, cancellationToken);

        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) =>
            _inner.CompleteIndexRunAsync(indexId, writeSet, completedAtUtc, cancellationToken);

        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) =>
            _inner.FailIndexRunAsync(indexId, failureMessage, completedAtUtc, cancellationToken);

        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedIndexAsync(indexId, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(
            CodebaseKind codebase,
            CodeChannel channel,
            string? environmentSnapshotId,
            CancellationToken cancellationToken) =>
            codebase == CodebaseKind.ScheduleI && channel == CodeChannel.Installed && environmentSnapshotId is null
                ? Task.FromResult<IndexRunRecord?>(_installedPinnedRun ?? _referenceGamePinnedRun)
                : _inner.GetLatestCompletedIndexAsync(codebase, channel, environmentSnapshotId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolPageAsync(string indexId, int offset, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolPageAsync(indexId, offset, limit, cancellationToken);

        public Task<int> CountCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.CountCompletedSymbolsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolByCanonicalKeyAsync(indexId, canonicalKey, cancellationToken);

        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolsByIdsAsync(indexId, symbolIds, cancellationToken);

        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) =>
            _inner.CountCompletedSymbolMatchesAsync(indexId, query, cancellationToken, kind);

        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) =>
            _inner.SearchCompletedSymbolsAsync(indexId, query, limit, cancellationToken, kind);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsBySourceSymbolIdAsync(indexId, symbolId, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, cancellationToken);

        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) =>
            _inner.CountCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, limit, cancellationToken);

        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) =>
            _inner.CountCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, limit, cancellationToken);

        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSourceFilesAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSourceLocationsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedFingerprintsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedCallableSurfaceAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceByGameSymbolIdAsync(string indexId, string gameSymbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedCallableSurfaceByGameSymbolIdAsync(indexId, gameSymbolId, cancellationToken);

        public Task<ReferenceIndexContextRecord?> GetReferenceIndexContextAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetReferenceIndexContextAsync(indexId, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedReferenceIndexAsync(string collection, CancellationToken cancellationToken) =>
            _referencePinnedRun is not null &&
            (_referenceCollection is not null && string.Equals(collection, _referenceCollection, StringComparison.Ordinal) ||
             string.Equals(collection, _referencePinnedRun.IndexId, StringComparison.Ordinal))
                ? Task.FromResult<IndexRunRecord?>(_referencePinnedRun)
                : _inner.GetLatestCompletedReferenceIndexAsync(collection, cancellationToken);

        public Task<IReadOnlyList<IndexRunRecord>> GetCompletedReferenceIndexesAsync(CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceIndexesAsync(cancellationToken);

        public Task<IReadOnlyList<IndexReferenceModRecord>> GetCompletedReferenceModsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceModsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceDocumentsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(string indexId, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceDocumentsAsync(indexId, limit, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> SearchCompletedReferenceDocumentsAsync(string indexId, string query, int limit, CancellationToken cancellationToken) =>
            _inner.SearchCompletedReferenceDocumentsAsync(indexId, query, limit, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) =>
            _inner.GetLatestCompletedIndexBySourceIdentityAsync(codebase, channel, sourceIdentity, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) =>
            _inner.GetLatestCompletedIndexForBuildAsync(codebase, channel, buildId, cancellationToken);

        public Task<string?> GetCompletedIndexBuildIdAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedIndexBuildIdAsync(indexId, cancellationToken);
    }

    private static class ToolArguments
    {
        public static bool TryValidateQuestion<T>(
            string? behavioralQuestion,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out ToolEnvelope<T> error) where T : class
        {
            if (!string.IsNullOrWhiteSpace(behavioralQuestion))
            {
                error = null!;
                return false;
            }

            error = Invalid<T>(
                authority,
                "InvalidQuestion",
                "The behavioral question must not be blank or whitespace.");
            return true;
        }

        public static bool TryValidateSelector<T>(
            string? selector,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out ToolEnvelope<T> error) where T : class
        {
            if (!string.IsNullOrWhiteSpace(selector))
            {
                error = null!;
                return false;
            }

            error = Invalid<T>(
                authority,
                "InvalidSelector",
                "The selector must not be blank or whitespace.");
            return true;
        }

        public static bool TryBoundRelationshipLimit<T>(
            int relationshipLimit,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (relationshipLimit is < 1 or > 50)
            {
                bounded = default;
                error = Invalid<T>(
                    authority,
                    "InvalidRelationshipLimit",
                    "The relationship evidence limit must be between 1 and 50.");
                return false;
            }

            bounded = relationshipLimit;
            error = null!;
            return true;
        }

        public static bool TryBoundOwnerLimit<T>(
            int ownerLimit,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (ownerLimit is < 1 or > 50)
            {
                bounded = default;
                error = Invalid<T>(
                    authority,
                    "InvalidOwnerLimit",
                    "The owner candidate limit must be between 1 and 50.");
                return false;
            }

            bounded = ownerLimit;
            error = null!;
            return true;
        }

        public static bool TryBoundContext<T>(
            int context,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (context < 0)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidContext", "Source context cannot be negative.");
                return false;
            }

            bounded = context;
            error = null!;
            return true;
        }

        public static bool TryBoundNativeBudget<T>(
            int budget,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (budget is < 0 or > 500)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidNativeTraversalBudget", "Native traversal budget must be between 0 and 500.");
                return false;
            }

            bounded = budget;
            error = null!;
            return true;
        }

        public static bool TryParseScope<T>(
            string? scope,
            string? collection,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out IndexQueryOptions options,
            out ToolEnvelope<T> error) where T : class
        {
            var parsedScope = string.IsNullOrWhiteSpace(scope)
                ? IndexQueryScope.Game
                : scope.Trim().ToLowerInvariant() switch
                {
                    "game" => IndexQueryScope.Game,
                    "reference" => IndexQueryScope.Reference,
                    "all" => IndexQueryScope.All,
                    _ => (IndexQueryScope?)null
                };
            if (parsedScope is null)
            {
                options = null!;
                error = Invalid<T>(authority, "InvalidScope", "Scope must be game, reference, or all.");
                return false;
            }

            var normalizedCollection = string.IsNullOrWhiteSpace(collection) ? null : collection.Trim();
            if (parsedScope == IndexQueryScope.Game && normalizedCollection is not null)
            {
                options = null!;
                error = Invalid<T>(authority, "InvalidCollection", "A collection is valid only for reference or all scope.");
                return false;
            }

            if (parsedScope is IndexQueryScope.Reference or IndexQueryScope.All && normalizedCollection is null)
            {
                options = null!;
                error = Invalid<T>(authority, "CollectionRequired", "Reference and all scope require an explicit collection.");
                return false;
            }

            options = new IndexQueryOptions(
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                false,
                50,
                parsedScope.Value,
                normalizedCollection);
            error = null!;
            return true;
        }

        private static ToolEnvelope<T> Invalid<T>(
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            string code,
            string message) where T : class =>
            ToolEnvelope<T>.Invalid(
                new ToolError(code, message),
                EnvelopeMapper.BuildFrom(authority),
                new ProvenanceEntry(
                    ProvenanceClassification.Fact,
                    "installed-build-authority",
                    authority.ResolvedBuildId,
                    authority.ExtractionId,
                    authority.IndexId),
                new ProvenanceEntry(
                    ProvenanceClassification.Derived,
                    "tool-argument-validation",
                    authority.ResolvedBuildId,
                    authority.ExtractionId,
                    authority.IndexId));
    }
}
