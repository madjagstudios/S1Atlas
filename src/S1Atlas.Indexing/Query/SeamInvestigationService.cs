using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class SeamInvestigationService
{
    private const int MaxTraversalDepth = 4;
    private readonly IndexQueryService _gameQuery;
    private readonly FederatedIndexQueryService _federatedQuery;
    private readonly ReferenceModQueryService _referenceQuery;
    private readonly INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest>? _nativeRepository;
    private readonly IAtlasRepository? _atlasRepository;

    public SeamInvestigationService(
        IndexQueryService gameQuery,
        FederatedIndexQueryService federatedQuery,
        ReferenceModQueryService referenceQuery,
        IIndexRepository? nativeRepository = null,
        IAtlasRepository? atlasRepository = null)
    {
        _gameQuery = gameQuery ?? throw new ArgumentNullException(nameof(gameQuery));
        _federatedQuery = federatedQuery ?? throw new ArgumentNullException(nameof(federatedQuery));
        _referenceQuery = referenceQuery ?? throw new ArgumentNullException(nameof(referenceQuery));
        _nativeRepository = nativeRepository as INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest>;
        _atlasRepository = atlasRepository;
    }

    public async Task<SeamInvestigationResult> InvestigateAsync(
        SeamInvestigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var boundedOptions = request.Options with { Limit = request.RelationshipLimit };
        var resolution = await ResolveAsync(request.Selector, request.Options, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
        {
            return new SeamInvestigationResult(
                request.BehavioralQuestion,
                SeamConclusion.InsufficientCoverage,
                resolution,
                null,
                "unknown",
                null,
                EvidenceCoverage.Unavailable,
                EvidenceCoverage.Unavailable,
                [new SeamEvidenceClaim(
                    "pinned build provenance",
                    SeamEvidenceClassification.Unknown,
                    "No completed index identity was resolved; build and extraction authority is supplied by the calling adapter when available.",
                    [])],
                [],
                [],
                [],
                ["canonical identity", "authority/entity attribution"],
                [new SeamNextAction("qualify-symbol", "Resolve the exact symbol before ownership analysis.", request.Selector, false)],
                BuildPinnedProvenance(request.Options, null),
                new SeamAuthorityEntityAttribution("UNKNOWN", "UNKNOWN", []),
                new SeamAlternateGenericCallerEvidence([], false, EvidenceCoverage.Unavailable, []),
                new SeamLifecyclePositionAndBeforeAfterState("UNKNOWN", "UNKNOWN", "UNKNOWN", EvidenceCoverage.Unavailable, []),
                new SeamApiBeforePatchResult("UNKNOWN", "callable surface could not be resolved", EvidenceCoverage.Unavailable, []))
                .ProjectDetails(request.IncludeDetails);
        }

        var source = await SourceAsync(resolution.Symbol, boundedOptions, request.SourceContext, cancellationToken, request.RelationshipLimit);
        var references = await ReferencesAsync(resolution.Symbol, boundedOptions, cancellationToken);
        var callers = await CallersAsync(resolution.Symbol, boundedOptions, cancellationToken);
        var callees = await CalleesAsync(resolution.Symbol, boundedOptions, cancellationToken);
        var callSites = await CallSitesAsync(resolution.Symbol, boundedOptions, cancellationToken);
        var fieldReferences = await FieldReferencesAsync(resolution.Symbol, boundedOptions, cancellationToken);
        var callableSurface = await CallableSurfaceAsync(resolution.Symbol, cancellationToken);

        var bodyStatus = source.Snippet?.BodyRecoveryStatus ?? callers.BodyRecoveryStatus ?? callees.BodyRecoveryStatus;
        var bodyCoverage = DetermineBodyCoverage(resolution.Symbol.Kind, bodyStatus, source.Snippet is not null);
        var callableCoverage = DetermineCallableCoverage(resolution.Symbol.Kind, callableSurface);
        var evidenceSections = BuildEvidenceSections(source, references, callers, callees, callSites, fieldReferences, bodyStatus);
        var ownerCandidateSet = await BuildOwnerCandidatesAsync(resolution.Symbol, boundedOptions, request.OwnerCandidateLimit, cancellationToken);
        var ownerCandidates = ownerCandidateSet.Candidates;
        var selectedCandidate = ownerCandidates.FirstOrDefault();
        var candidateRole = selectedCandidate?.Role ?? "unknown";
        var pinnedProvenance = BuildPinnedProvenance(request.Options, resolution.Symbol);
        var authorityEntityAttribution = BuildAuthorityEntityAttribution(
            selectedCandidate,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            evidenceSections);
        var alternateCallers = BuildAlternateCallerEvidence(
            ownerCandidates,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            evidenceSections);
        var lifecycle = BuildLifecycleEvidence(resolution.Symbol, selectedCandidate, bodyStatus);
        var apiBeforePatch = BuildApiBeforePatchEvidence(resolution.Symbol);
        var nativeEvidence = await GetNativeEvidenceAsync(request, resolution.Symbol, cancellationToken);
        var unknownDimensions = BuildUnknownDimensions(
            resolution.Symbol,
            selectedCandidate,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            evidenceSections,
            bodyCoverage,
            callableCoverage,
            lifecycle,
            apiBeforePatch);
        var coverageWarnings = BuildCoverageWarnings(
            selectedCandidate,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            evidenceSections,
            bodyCoverage,
            callableCoverage,
            lifecycle,
            apiBeforePatch,
            nativeEvidence);
        var claims = BuildClaims(
            request,
            resolution.Symbol,
            selectedCandidate,
            ownerCandidates,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            bodyStatus,
            evidenceSections,
            unknownDimensions);
        var nextActions = BuildNextActions(resolution.Symbol, bodyCoverage, evidenceSections, unknownDimensions, nativeEvidence);
        var conclusion = DetermineConclusion(
            selectedCandidate,
            ownerCandidates,
            ownerCandidateSet.TotalCount,
            ownerCandidateSet.Coverage,
            coverageWarnings,
            unknownDimensions,
            bodyCoverage,
            callableCoverage,
            evidenceSections,
            lifecycle.Coverage,
            apiBeforePatch.Coverage);

        return new SeamInvestigationResult(
            request.BehavioralQuestion,
            conclusion,
            resolution,
            selectedCandidate?.Symbol,
            candidateRole,
            bodyStatus,
            bodyCoverage,
            callableCoverage,
            claims,
            evidenceSections,
            ownerCandidates,
            coverageWarnings,
            unknownDimensions,
            nextActions,
            pinnedProvenance,
            authorityEntityAttribution,
            alternateCallers,
            lifecycle,
            apiBeforePatch,
            nativeEvidence)
            .ProjectDetails(request.IncludeDetails);
    }

    private async Task<NativeEvidenceSummary?> GetNativeEvidenceAsync(
        SeamInvestigationRequest request,
        SymbolQueryResult resolved,
        CancellationToken cancellationToken)
    {
        if (request.NativeTraversalBudget == 0 ||
            request.NativeSymbolIds is null ||
            request.NativeSymbolIds.Count == 0 ||
            !IsGameSymbol(resolved) ||
            _nativeRepository is null ||
            _atlasRepository is null)
        {
            return null;
        }

        var buildId = await GetBuildIdAsync(resolved.IndexId, cancellationToken);
        var snapshot = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        if (buildId is null || snapshot is null ||
            !string.Equals(snapshot.Build.BuildId, buildId, StringComparison.Ordinal))
        {
            return new NativeEvidenceSummary(
                NativeRecoveryStatus.InputChanged,
                NativeEvidenceLookupStatus.InputChanged,
                false,
                [],
                [],
                [],
                "native-recovery-input-authority",
                string.Empty);
        }

        if (_nativeRepository is IIndexRepository indexRepository)
        {
            var completedIndex = await indexRepository.GetCompletedIndexAsync(
                resolved.IndexId,
                cancellationToken);
            var codeSnapshot = completedIndex is null
                ? null
                : await indexRepository.GetCodeSnapshotAsync(
                    completedIndex.SnapshotId,
                    cancellationToken);
            if (snapshot.SnapshotId is null ||
                codeSnapshot is null ||
                !string.Equals(
                    codeSnapshot.EnvironmentSnapshotId,
                    snapshot.SnapshotId,
                    StringComparison.Ordinal))
            {
                return new NativeEvidenceSummary(
                    NativeRecoveryStatus.InputChanged,
                    NativeEvidenceLookupStatus.InputChanged,
                    false,
                    [],
                    [],
                    [],
                    "native-recovery-input-authority",
                    string.Empty);
            }
        }

        var nativeRequest = new NativeRecoveryRequest(
            buildId,
            resolved.IndexId,
            snapshot.Build.GameAssemblySha256,
            request.NativeSymbolIds,
            request.NativeTraversalBudget);
        var records = await _nativeRepository.GetNativeRecoveriesAsync(nativeRequest, cancellationToken);
        var record = records.FirstOrDefault();
        return record is null
            ? new NativeEvidenceSummary(
                NativeRecoveryStatus.Unsupported,
                NativeEvidenceLookupStatus.NoMatch,
                false,
                [],
                [],
                [],
                "native-recovery-record-not-found",
                string.Empty)
            : new NativeEvidenceSummary(
                record.Status,
                NativeEvidenceLookupStatus.Matched,
                record.IsComplete,
                record.MappingEvidence,
                record.Edges.Where(edge => edge.Kind == "DirectCall").ToArray(),
                record.FieldAccesses,
                $"{record.ToolName} {record.ToolVersion} ({record.ToolSha256})",
                record.OutputSha256,
                record.FailureMessage);
    }

    private async Task<string?> GetBuildIdAsync(
        string indexId,
        CancellationToken cancellationToken)
    {
        if (_nativeRepository is IIndexRepository indexRepository)
            return await indexRepository.GetCompletedIndexBuildIdAsync(indexId, cancellationToken);
        return null;
    }

    private async Task<SymbolResolutionResult> ResolveAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Reference && options.Codebase == CodebaseKind.ReferenceMod)
            return await _referenceQuery.ResolveAsync(selector, options, cancellationToken);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.ResolveAsync(selector, options, cancellationToken);
        return await _gameQuery.ResolveAsync(selector, options, cancellationToken);
    }

    private async Task<SourceSnippetResolutionResult> SourceAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        int context,
        CancellationToken cancellationToken,
        int relatedLimit)
    {
        if (options.Scope == IndexQueryScope.Game && IsGameSymbol(symbol))
        {
            return await _gameQuery.SourceInIndexAsync(
                PinnedRun(symbol),
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                symbol.SymbolId,
                context,
                cancellationToken,
                relatedLimit: relatedLimit);
        }
        if (options.Scope == IndexQueryScope.Reference && options.Codebase == CodebaseKind.ReferenceMod)
            return await _referenceQuery.SourceAsync(symbol.SymbolId, options, context, cancellationToken, relatedLimit: relatedLimit);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.SourceAsync(symbol.SymbolId, options, context, cancellationToken, relatedLimit: relatedLimit);
        return await _gameQuery.SourceAsync(symbol.SymbolId, options, context, cancellationToken, relatedLimit: relatedLimit);
    }

    private async Task<RelationshipQuerySetResult> ReferencesAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game && IsGameSymbol(symbol))
            return await _gameQuery.RefsInIndexAsync(PinnedRun(symbol), CodebaseKind.ScheduleI, CodeChannel.Installed, symbol.SymbolId, options.Limit, cancellationToken);
        if (options.Scope == IndexQueryScope.Reference && options.Codebase == CodebaseKind.ReferenceMod)
            return await _referenceQuery.RefsAsync(symbol.SymbolId, options, cancellationToken);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.RefsAsync(symbol.SymbolId, options, cancellationToken);
        return await _gameQuery.RefsAsync(symbol.SymbolId, options, cancellationToken);
    }

    private async Task<RelationshipQuerySetResult> CallersAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game && IsGameSymbol(symbol))
            return await _gameQuery.CallersInIndexAsync(PinnedRun(symbol), CodebaseKind.ScheduleI, CodeChannel.Installed, symbol.SymbolId, options.Limit, cancellationToken);
        if (options.Scope == IndexQueryScope.Reference && options.Codebase == CodebaseKind.ReferenceMod)
            return await _referenceQuery.CallersAsync(symbol.SymbolId, options, cancellationToken);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.CallersAsync(symbol.SymbolId, options, cancellationToken);
        return await _gameQuery.CallersAsync(symbol.SymbolId, options, cancellationToken);
    }

    private async Task<RelationshipQuerySetResult> CalleesAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game && IsGameSymbol(symbol))
            return await _gameQuery.CalleesInIndexAsync(PinnedRun(symbol), CodebaseKind.ScheduleI, CodeChannel.Installed, symbol.SymbolId, options.Limit, cancellationToken);
        if (options.Scope == IndexQueryScope.Reference && options.Codebase == CodebaseKind.ReferenceMod)
            return await _referenceQuery.CalleesAsync(symbol.SymbolId, options, cancellationToken);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.CalleesAsync(symbol.SymbolId, options, cancellationToken);
        return await _gameQuery.CalleesAsync(symbol.SymbolId, options, cancellationToken);
    }

    private async Task<CallSiteQueryResult> CallSitesAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Game && IsGameSymbol(symbol))
            return await _gameQuery.CallSitesInIndexAsync(PinnedRun(symbol), CodebaseKind.ScheduleI, CodeChannel.Installed, symbol.SymbolId, options.Limit, cancellationToken);
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.CallSitesAsync(symbol.SymbolId, options, cancellationToken);
        return await _gameQuery.CallSitesAsync(symbol.SymbolId, options, cancellationToken);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (IsCallable(symbol.Kind))
        {
            // Refs are read without the service's output limit so direction and totals are
            // exact; only the returned evidence page is bounded below.
            var references = await AllReferencesForFieldAsync(symbol, options, cancellationToken);
            var outgoing = references.Relationships
                .Where(edge => string.Equals(edge.Direction, "Outgoing", StringComparison.Ordinal))
                .Where(edge => string.Equals(edge.Kind, "ReadsField", StringComparison.Ordinal) || string.Equals(edge.Kind, "WritesField", StringComparison.Ordinal))
                .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
            var returned = outgoing.Take(options.Limit).ToArray();
            return new FieldReferenceQueryResult(
                references.Resolution,
                new RelationshipQueryPageResult(outgoing.Length, returned.Length, returned));
        }
        if (options.Scope != IndexQueryScope.Game)
            return await _federatedQuery.FieldReferencesAsync(symbol.SymbolId, options, FieldReferenceFilter.All, cancellationToken);
        return await _gameQuery.FieldReferencesAsync(symbol.SymbolId, options, FieldReferenceFilter.All, cancellationToken);
    }

    private async Task<RelationshipQuerySetResult> AllReferencesForFieldAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Scope == IndexQueryScope.Reference &&
            string.Equals(symbol.Origin, "reference", StringComparison.Ordinal))
        {
            // Field totals must come from the complete reference edge set. The service applies
            // RelationshipLimit only after filtering outgoing field edges for the returned page.
            return await _referenceQuery.RefsAsync(
                symbol.SymbolId,
                options with { Limit = int.MaxValue },
                cancellationToken);
        }

        if (options.Scope != IndexQueryScope.Game)
        {
            return await _federatedQuery.RefsAsync(
                symbol.SymbolId,
                options with { Limit = int.MaxValue },
                cancellationToken);
        }

        if (IsGameSymbol(symbol))
        {
            return await _gameQuery.RefsInIndexAsync(
                PinnedRun(symbol),
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                symbol.SymbolId,
                int.MaxValue,
                cancellationToken);
        }
        return await _gameQuery.RefsAsync(symbol.SymbolId, options with { Limit = int.MaxValue }, cancellationToken);
    }

    private async Task<CallableSurfaceResolutionResult?> CallableSurfaceAsync(
        SymbolQueryResult symbol,
        CancellationToken cancellationToken)
    {
        if (!IsCallable(symbol.Kind) ||
            !string.Equals(symbol.Codebase, CodebaseKind.ScheduleI.ToString(), StringComparison.Ordinal) ||
            !string.Equals(symbol.Channel, CodeChannel.Installed.ToString(), StringComparison.Ordinal))
        {
            return null;
        }

        var pinnedRun = new IndexRunRecord(
            symbol.IndexId,
            string.Empty,
            IndexRunStatus.Completed,
            string.Empty);
        return await _gameQuery.GetCallableSurfaceInIndexAsync(
            pinnedRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            symbol.SymbolId,
            cancellationToken);
    }

    private async Task<OwnerCandidateSet> BuildOwnerCandidatesAsync(
        SymbolQueryResult resolved,
        IndexQueryOptions options,
        int ownerCandidateLimit,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<TraversalState>();
        var paths = new Dictionary<TraversalKey, TraversalPath>();
        var incomingCache = new Dictionary<TraversalKey, IncomingTraversalResult>();
        var canonicalKeys = await LoadCanonicalKeysAsync(resolved, options, cancellationToken);
        var remainingTraversalEdges = options.Limit;
        var resolvedKey = TraversalKey.For(resolved);
        var traversalCoverage = EvidenceCoverage.Complete;
        queue.Enqueue(new TraversalState(resolvedKey, 0));

        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            if (state.Depth >= MaxTraversalDepth)
            {
                // The node itself remains a reachable candidate, but its incoming owners were
                // not inspected. Do not allow a truncated reverse traversal to prove exclusivity.
                traversalCoverage = EvidenceCoverage.Incomplete;
                continue;
            }

            var stateSymbol = state.Key == resolvedKey
                ? resolved
                : paths[state.Key].Symbol;
            if (remainingTraversalEdges == 0)
            {
                traversalCoverage = EvidenceCoverage.Incomplete;
                break;
            }

            var incomingResult = await GetIncomingEdgesAsync(stateSymbol, options, incomingCache, cancellationToken);
            if (incomingResult.Coverage != EvidenceCoverage.Complete)
                traversalCoverage = EvidenceCoverage.Incomplete;
            var incoming = incomingResult.Relationships
                .Take(remainingTraversalEdges)
                .ToArray();
            remainingTraversalEdges -= incoming.Length;
            if (incomingResult.Relationships.Count > incoming.Length)
                traversalCoverage = EvidenceCoverage.Incomplete;
            if (incoming.Length == 0)
                continue;

            foreach (var edge in incoming)
            {
                if (edge.Source.SymbolId is null || !edge.Source.Resolved)
                    continue;

                var source = ToSourceSymbol(edge.Source, resolved, canonicalKeys);
                if (!canonicalKeys.TryGetValue(source, out var canonicalKey))
                    throw new InvalidDataException($"The resolved relationship source '{source.SymbolId}' has no persisted canonical key.");
                var sourceKey = TraversalKey.For(source);
                var nextPath = paths.TryGetValue(state.Key, out var parentPath)
                    ? parentPath.Prepend(source, canonicalKey, edge)
                    : TraversalPath.Start(source, canonicalKey, edge);
                if (!paths.TryGetValue(sourceKey, out var existing) || nextPath.IsBetterThan(existing))
                {
                    paths[sourceKey] = nextPath;
                    queue.Enqueue(new TraversalState(sourceKey, state.Depth + 1));
                }
            }
        }

        var candidates = paths.Values
            .Where(path => IsOwnerCandidateKind(path.Symbol.Kind))
            .OrderBy(path => path.PathLength)
            .ThenByDescending(path => path.SupportingRelationshipFamilyCount)
            .ThenBy(path => path.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(path => path.Symbol.SymbolId, StringComparer.Ordinal)
            .Select(path => new SeamOwnerCandidate(
                path.Symbol,
                ClassifyRole(path.Symbol),
                new SeamEvidencePath(path.RelationshipIds, path.PathLength, path.SupportingRelationshipFamilyCount),
                path.RelationshipIds))
            .ToArray();
        return new OwnerCandidateSet(candidates.Take(ownerCandidateLimit).ToArray(), candidates.Length, traversalCoverage);
    }

    private async Task<CanonicalKeyCatalog> LoadCanonicalKeysAsync(
        SymbolQueryResult resolved,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CodebaseKind>(resolved.Codebase, out var codebase) ||
            !Enum.TryParse<CodeChannel>(resolved.Channel, out var channel))
        {
            throw new InvalidDataException($"The resolved symbol '{resolved.SymbolId}' has an unsupported codebase or channel.");
        }

        // The listing API returns the persisted key while SymbolQueryResult intentionally carries
        // only query/display fields. Resolution already pins the completed index ID.
        var run = new IndexRunRecord(resolved.IndexId, string.Empty, IndexRunStatus.Completed, string.Empty);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        await LoadCanonicalKeysAsync(keys, run, codebase, channel, cancellationToken);
        string? gameIndexId = codebase == CodebaseKind.ScheduleI ? resolved.IndexId : null;
        string? referenceIndexId = codebase == CodebaseKind.ReferenceMod ? resolved.IndexId : null;
        if (options.Scope != IndexQueryScope.Game)
        {
            var selection = await _referenceQuery.GetSelectionForFederationAsync(options, cancellationToken);
            if (selection is not null)
            {
                await LoadCanonicalKeysAsync(
                    keys,
                    new IndexRunRecord(selection.GameRun.IndexId, string.Empty, IndexRunStatus.Completed, string.Empty),
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    cancellationToken);
                gameIndexId = selection.GameRun.IndexId;
                await LoadCanonicalKeysAsync(
                    keys,
                    new IndexRunRecord(selection.Run.IndexId, string.Empty, IndexRunStatus.Completed, string.Empty),
                    CodebaseKind.ReferenceMod,
                    CodeChannel.Installed,
                    cancellationToken);
                referenceIndexId = selection.Run.IndexId;
            }
        }

        return new CanonicalKeyCatalog(keys, resolved.IndexId, gameIndexId, referenceIndexId);
    }

    private async Task LoadCanonicalKeysAsync(
        IDictionary<string, string> keys,
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        CancellationToken cancellationToken)
    {
        if (keys.Keys.Any(key => key.StartsWith(run.IndexId + "\u001f", StringComparison.Ordinal)))
            return;

        const int pageSize = 512;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await _gameQuery.ListSymbolsInIndexAsync(
                run,
                codebase,
                channel,
                new IndexPageRequest(offset, pageSize),
                cancellationToken);
            foreach (var symbol in page.Results)
                keys[run.IndexId + "\u001f" + symbol.SymbolId] = symbol.CanonicalKey;
            if (!page.HasMore)
                break;
        }
    }

    private async Task<IncomingTraversalResult> GetIncomingEdgesAsync(
        SymbolQueryResult symbol,
        IndexQueryOptions options,
        IDictionary<TraversalKey, IncomingTraversalResult> cache,
        CancellationToken cancellationToken)
    {
        var key = TraversalKey.For(symbol);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var callers = await CallersAsync(symbol, options, cancellationToken);
        var incoming = callers.Relationships
            .Where(edge => string.Equals(edge.Direction, "Incoming", StringComparison.Ordinal))
            .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var result = new IncomingTraversalResult(
            incoming,
            callers.TotalCount is int totalCount && callers.Relationships.Count >= totalCount
                ? EvidenceCoverage.Complete
                : EvidenceCoverage.Incomplete);
        cache[key] = result;
        return result;
    }

    private static IReadOnlyList<SeamEvidenceSection> BuildEvidenceSections(
        SourceSnippetResolutionResult source,
        RelationshipQuerySetResult references,
        RelationshipQuerySetResult callers,
        RelationshipQuerySetResult callees,
        CallSiteQueryResult callSites,
        FieldReferenceQueryResult fieldReferences,
        BodyRecoveryStatus? bodyStatus)
    {
        var neighborhood = source.Snippet?.Neighborhood;

        return
        [
            CreateRelationshipSection("References", references, bodyStatus, false),
            neighborhood is null
                ? CreateRelationshipSection("Callers", callers, bodyStatus, true)
                : CreateSection(
                    "Callers",
                    neighborhood.CallerTotal,
                    neighborhood.Callers.Count,
                    neighborhood.Callers.Select(edge => edge.RelationshipId),
                    neighborhood.CallerCompletenessNotice,
                    bodyStatus,
                    true),
            neighborhood is null
                ? CreateRelationshipSection("Callees", callees, bodyStatus, true)
                : CreateSection(
                    "Callees",
                    neighborhood.CalleeTotal,
                    neighborhood.Callees.Count,
                    neighborhood.Callees.Select(edge => edge.RelationshipId),
                    neighborhood.CalleeCompletenessNotice,
                    bodyStatus,
                    true),
            CreateSection("CallSites", callSites.TotalCount, callSites.ReturnedCount, callSites.Relationships.Select(edge => edge.RelationshipId), callSites.CompletenessNotice, bodyStatus, false),
            CreateSection("FieldReferences", fieldReferences.TotalCount, fieldReferences.ReturnedCount, fieldReferences.Relationships.Select(edge => edge.RelationshipId), fieldReferences.CompletenessNotice, bodyStatus, false)
        ];
    }

    private static SeamEvidenceSection CreateRelationshipSection(
        string family,
        RelationshipQuerySetResult result,
        BodyRecoveryStatus? bodyStatus,
        bool callLike)
    {
        var notice = result.CompletenessNotice;
        if (result.TotalCount is null)
            notice = string.IsNullOrWhiteSpace(notice)
                ? TargetRelationshipQueryNotices.RelationshipTotalUnavailable
                : notice + " " + TargetRelationshipQueryNotices.RelationshipTotalUnavailable;
        return CreateSection(
            family,
            result.TotalCount ?? result.Relationships.Count,
            result.Relationships.Count,
            result.Relationships.Select(edge => edge.RelationshipId),
            notice,
            bodyStatus,
            callLike,
            result.TotalCount is not null);
    }

    private static SeamEvidenceSection CreateSection(
        string family,
        int totalCount,
        int returnedCount,
        IEnumerable<string> evidenceIds,
        string? notice,
        BodyRecoveryStatus? bodyStatus,
        bool callLike,
        bool totalCountKnown = true)
    {
        var ids = evidenceIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var coverage = !totalCountKnown || returnedCount < totalCount
            ? EvidenceCoverage.Incomplete
            : totalCount == 0 && callLike && bodyStatus is BodyRecoveryStatus.StubOrUnavailable or BodyRecoveryStatus.Unknown
                ? EvidenceCoverage.Unavailable
                : EvidenceCoverage.Complete;
        return new SeamEvidenceSection(family, coverage, totalCount, returnedCount, ids, string.IsNullOrWhiteSpace(notice) ? null : notice);
    }

    private static EvidenceCoverage DetermineBodyCoverage(
        string symbolKind,
        BodyRecoveryStatus? bodyStatus,
        bool hasSnippet)
    {
        if (!IsCallable(symbolKind))
            return EvidenceCoverage.NotApplicable;
        if (!hasSnippet)
            return EvidenceCoverage.Unavailable;
        return bodyStatus is BodyRecoveryStatus.Recovered or BodyRecoveryStatus.NoBodyByDesign
            ? EvidenceCoverage.Complete
            : EvidenceCoverage.Unavailable;
    }

    private static EvidenceCoverage DetermineCallableCoverage(
        string symbolKind,
        CallableSurfaceResolutionResult? callableSurface)
    {
        if (!IsCallable(symbolKind))
            return EvidenceCoverage.NotApplicable;
        return callableSurface?.Resolution.Status == SymbolResolutionStatus.Resolved &&
               callableSurface.CallableSurface?.Status == CallableSurfaceStatus.Resolved.ToString()
            ? EvidenceCoverage.Complete
            : EvidenceCoverage.Unavailable;
    }

    private static IReadOnlyList<string> BuildUnknownDimensions(
        SymbolQueryResult resolved,
        SeamOwnerCandidate? selectedCandidate,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        IReadOnlyList<SeamEvidenceSection> sections,
        EvidenceCoverage bodyCoverage,
        EvidenceCoverage callableCoverage,
        SeamLifecyclePositionAndBeforeAfterState lifecycle,
        SeamApiBeforePatchResult apiBeforePatch)
    {
        var unknowns = new List<string>();
        if (resolved.IsBestEffort)
            unknowns.Add("canonical identity");
        if (selectedCandidate is null ||
            selectedCandidate.Role == "unknown" ||
            ownerCandidateTotalCount != 1 ||
            ownerCandidateCoverage != EvidenceCoverage.Complete)
            unknowns.Add("authority/entity attribution");
        if (ownerCandidateTotalCount != 1 ||
            ownerCandidateCoverage != EvidenceCoverage.Complete ||
            sections.Any(section => section.Family == "Callers" && section.Coverage != EvidenceCoverage.Complete))
            unknowns.Add("exclusivity");
        if (IsCallable(resolved.Kind) && callableCoverage != EvidenceCoverage.Complete)
            unknowns.Add("native substrate");
        if (lifecycle.Coverage != EvidenceCoverage.Complete && lifecycle.Coverage != EvidenceCoverage.NotApplicable)
            unknowns.Add("lifecycle position and before/after state");
        if (apiBeforePatch.Coverage != EvidenceCoverage.Complete && apiBeforePatch.Coverage != EvidenceCoverage.NotApplicable)
            unknowns.Add("api coverage");
        return unknowns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static SeamPinnedProvenance BuildPinnedProvenance(
        IndexQueryOptions options,
        SymbolQueryResult? resolved)
    {
        return new SeamPinnedProvenance(
            RequestedBuildId: null,
            ResolvedBuildId: null,
            ExtractionId: null,
            IndexId: resolved?.IndexId,
            Codebase: resolved?.Codebase ?? options.Codebase.ToString(),
            Channel: resolved?.Channel ?? options.Channel?.ToString(),
            IntegrityVerified: false);
    }

    private static SeamAuthorityEntityAttribution BuildAuthorityEntityAttribution(
        SeamOwnerCandidate? selectedCandidate,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        IReadOnlyList<SeamEvidenceSection> sections)
    {
        var callersComplete = sections.Any(section =>
            section.Family == "Callers" && section.Coverage == EvidenceCoverage.Complete);
        var evidenceBacked = selectedCandidate is not null &&
            selectedCandidate.EvidenceIds.Count > 0 &&
            selectedCandidate.Role.Length > 0 &&
            !string.Equals(selectedCandidate.Role, "unknown", StringComparison.Ordinal) &&
            ownerCandidateTotalCount == 1 &&
            ownerCandidateCoverage == EvidenceCoverage.Complete &&
            callersComplete;
        var authority = evidenceBacked
            ? selectedCandidate!.Symbol.Codebase + ":" + selectedCandidate.Symbol.Channel
            : "UNKNOWN";
        var entity = evidenceBacked ? selectedCandidate!.Symbol.QualifiedName : "UNKNOWN";
        return new SeamAuthorityEntityAttribution(
            authority,
            entity,
            evidenceBacked ? selectedCandidate!.EvidenceIds : []);
    }

    private static SeamAlternateGenericCallerEvidence BuildAlternateCallerEvidence(
        IReadOnlyList<SeamOwnerCandidate> ownerCandidates,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        IReadOnlyList<SeamEvidenceSection> sections)
    {
        var callerSection = sections.FirstOrDefault(section => section.Family == "Callers");
        var coverage = callerSection?.Coverage ?? EvidenceCoverage.Unavailable;
        return new SeamAlternateGenericCallerEvidence(
            ownerCandidates,
            coverage == EvidenceCoverage.Complete &&
            ownerCandidateCoverage == EvidenceCoverage.Complete &&
            ownerCandidateTotalCount == 1,
            coverage,
            callerSection?.EvidenceIds ?? []);
    }

    private static SeamLifecyclePositionAndBeforeAfterState BuildLifecycleEvidence(
        SymbolQueryResult resolved,
        SeamOwnerCandidate? selectedCandidate,
        BodyRecoveryStatus? bodyStatus)
    {
        if (!IsCallable(resolved.Kind))
        {
            return new SeamLifecyclePositionAndBeforeAfterState(
                "UNKNOWN",
                "UNKNOWN",
                "UNKNOWN",
                EvidenceCoverage.Unavailable,
                []);
        }

        return new SeamLifecyclePositionAndBeforeAfterState(
            selectedCandidate?.Role ?? "UNKNOWN",
            "UNKNOWN",
            "UNKNOWN",
            EvidenceCoverage.Unavailable,
            bodyStatus is null ? [] : [$"body:{resolved.SymbolId}"]);
    }

    private static SeamApiBeforePatchResult BuildApiBeforePatchEvidence(SymbolQueryResult resolved)
    {
        if (!IsCallable(resolved.Kind))
        {
            return new SeamApiBeforePatchResult(
                "UNKNOWN",
                "API-before-patch evidence is unavailable for the selected non-callable seam.",
                EvidenceCoverage.Unavailable,
                []);
        }

        return new SeamApiBeforePatchResult(
            "UNKNOWN",
            "S1API/S1MAPI evidence is unavailable; a callable wrapper is not API-before-patch evidence.",
            EvidenceCoverage.Unavailable,
            []);
    }

    private static IReadOnlyList<string> BuildCoverageWarnings(
        SeamOwnerCandidate? selectedCandidate,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        IReadOnlyList<SeamEvidenceSection> sections,
        EvidenceCoverage bodyCoverage,
        EvidenceCoverage callableCoverage,
        SeamLifecyclePositionAndBeforeAfterState lifecycle,
        SeamApiBeforePatchResult apiBeforePatch,
        NativeEvidenceSummary? nativeEvidence = null)
    {
        var warnings = new List<string>();
        if (bodyCoverage != EvidenceCoverage.Complete && bodyCoverage != EvidenceCoverage.NotApplicable)
            warnings.Add("Escalation: missing body coverage");
        var callers = sections.FirstOrDefault(section => section.Family == "Callers");
        if (callers is not null && callers.Coverage == EvidenceCoverage.Incomplete)
            warnings.Add("Escalation: incomplete caller coverage");
        if (callers is not null && callers.Coverage == EvidenceCoverage.Unavailable)
            warnings.Add("Escalation: missing caller coverage");
        if (callableCoverage == EvidenceCoverage.Unavailable)
            warnings.Add("Escalation: API callable coverage is unavailable");
        if (ownerCandidateCoverage != EvidenceCoverage.Complete)
            warnings.Add("Escalation: owner candidate traversal coverage is incomplete");
        if (ownerCandidateTotalCount > 1)
            warnings.Add("Escalation: multiple owner candidates remain in scope");
        if (lifecycle.Coverage == EvidenceCoverage.Unavailable)
            warnings.Add("Escalation: lifecycle position and before/after state are unavailable");
        if (apiBeforePatch.Coverage == EvidenceCoverage.Unavailable)
            warnings.Add("Escalation: API before-patch coverage is unavailable");
        if (nativeEvidence?.Status == NativeRecoveryStatus.Failed)
            warnings.Add("Escalation: native recovery failed");
        if (selectedCandidate is null || selectedCandidate.Role == "unknown")
            warnings.Add("Escalation: unresolved owning authority");
        return warnings
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<SeamEvidenceClaim> BuildClaims(
        SeamInvestigationRequest request,
        SymbolQueryResult resolved,
        SeamOwnerCandidate? selectedCandidate,
        IReadOnlyList<SeamOwnerCandidate> ownerCandidates,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        BodyRecoveryStatus? bodyStatus,
        IReadOnlyList<SeamEvidenceSection> sections,
        IReadOnlyList<string> unknownDimensions)
    {
        var claims = new List<SeamEvidenceClaim>
        {
            new(
                "candidate symbol",
                SeamEvidenceClassification.Fact,
                $"Resolved target symbol '{resolved.QualifiedName}' for '{request.BehavioralQuestion}'.",
                [$"resolution:{resolved.SymbolId}"])
        };

        if (bodyStatus is not null)
        {
            claims.Add(new SeamEvidenceClaim(
                "body/callability coverage",
                SeamEvidenceClassification.Fact,
                $"Body recovery status is '{bodyStatus}'.",
                [$"body:{resolved.SymbolId}"]));
        }

        claims.Add(new SeamEvidenceClaim(
            "pinned build provenance",
            SeamEvidenceClassification.Unknown,
            "The seam evidence is pinned to the completed index identity; build and extraction authority is supplied by the calling adapter when available.",
            [$"resolution:{resolved.SymbolId}"]));

        if (selectedCandidate is not null)
        {
            claims.Add(new SeamEvidenceClaim(
                "role",
                selectedCandidate.Role == "unknown"
                    ? SeamEvidenceClassification.Unknown
                    : SeamEvidenceClassification.Derived,
                $"Candidate '{selectedCandidate.Symbol.QualifiedName}' is classified as '{selectedCandidate.Role}'.",
                selectedCandidate.EvidenceIds));
        }

        var ownerCoverageIncomplete = ownerCandidateCoverage != EvidenceCoverage.Complete;
        var ownerLimitHidesCandidates = ownerCandidateTotalCount > ownerCandidates.Count;
        if (sections.Any(section => section.Family == "Callers" && section.Coverage != EvidenceCoverage.Complete) ||
            ownerCoverageIncomplete ||
            ownerLimitHidesCandidates)
        {
            claims.Add(new SeamEvidenceClaim(
                "alternate/generic callers and exclusivity",
                SeamEvidenceClassification.Unknown,
                ownerCandidateTotalCount > 1
                    ? ownerLimitHidesCandidates
                        ? "Owner candidate output was limited before exclusivity could be established."
                        : "Incomplete owner coverage leaves multiple owner candidates unresolved."
                    : ownerCoverageIncomplete
                        ? "Incomplete owner coverage leaves owner exclusivity unresolved."
                        : sections.Any(section => section.Family == "Callers" && section.Coverage != EvidenceCoverage.Complete)
                            ? "Incomplete caller coverage leaves owner exclusivity unresolved."
                            : "Owner exclusivity remains unresolved.",
                sections.Where(section => section.Family == "Callers").SelectMany(section => section.EvidenceIds).ToArray()));
        }
        else if (ownerCandidates.Count > 1 && sections.Any(section => section.Family == "Callers"))
        {
            claims.Add(new SeamEvidenceClaim(
                "alternate/generic callers and exclusivity",
                SeamEvidenceClassification.Derived,
                "Multiple owner candidates remain in scope and static evidence does not prove exclusivity.",
                sections.Where(section => section.Family == "Callers").SelectMany(section => section.EvidenceIds).ToArray()));
        }
        else if (selectedCandidate is not null && sections.Any(section => section.Family == "Callers"))
        {
            claims.Add(new SeamEvidenceClaim(
                "alternate/generic callers and exclusivity",
                SeamEvidenceClassification.Derived,
                "One owner candidate remains in the inspected scope; exclusivity is based on bounded static evidence.",
                sections.Where(section => section.Family == "Callers").SelectMany(section => section.EvidenceIds).ToArray()));
        }

        if (unknownDimensions.Count > 0)
        {
            claims.Add(new SeamEvidenceClaim(
                "remaining UNKNOWNs",
                SeamEvidenceClassification.Unknown,
                $"Remaining unknown dimensions: {string.Join(", ", unknownDimensions)}.",
                [$"resolution:{resolved.SymbolId}"]));
        }

        return claims;
    }

    private static IReadOnlyList<SeamNextAction> BuildNextActions(
        SymbolQueryResult resolved,
        EvidenceCoverage bodyCoverage,
        IReadOnlyList<SeamEvidenceSection> sections,
        IReadOnlyList<string> unknownDimensions,
        NativeEvidenceSummary? nativeEvidence = null)
    {
        var actions = new List<SeamNextAction>();
        if (unknownDimensions.Any(value => value.Contains("api coverage", StringComparison.OrdinalIgnoreCase)))
        {
            actions.Add(new SeamNextAction(
                "api-lookup",
                "API ownership coverage remains unknown.",
                resolved.QualifiedName,
                false));
        }

        if (nativeEvidence?.LookupStatus is NativeEvidenceLookupStatus.NoMatch or NativeEvidenceLookupStatus.InputChanged ||
            nativeEvidence?.Status == NativeRecoveryStatus.NoBody ||
            nativeEvidence?.Status == NativeRecoveryStatus.Failed ||
            bodyCoverage != EvidenceCoverage.Complete && bodyCoverage != EvidenceCoverage.NotApplicable ||
            unknownDimensions.Any(value => value.Contains("native substrate", StringComparison.OrdinalIgnoreCase)))
        {
            actions.Add(new SeamNextAction(
                "targeted-native-recovery",
                nativeEvidence?.LookupStatus == NativeEvidenceLookupStatus.NoMatch
                    ? "No matching native recovery record exists for the selected identity and symbol set."
                    : nativeEvidence?.LookupStatus == NativeEvidenceLookupStatus.InputChanged
                        ? "The native recovery input identity changed; refresh the selected build and index."
                        : nativeEvidence?.Status == NativeRecoveryStatus.Failed
                            ? "Native recovery failed; inspect its bounded failure message before retrying."
                            : nativeEvidence?.Status == NativeRecoveryStatus.NoBody
                    ? "Native recovery found no recoverable body; inspect the bounded negative result before escalating."
                    : "Native substrate evidence is unavailable for the selected seam.",
                resolved.QualifiedName,
                false));
        }

        if (unknownDimensions.Any(value => value.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)) ||
            sections.Any(section => section.Family == "Callers" && section.Coverage != EvidenceCoverage.Complete))
        {
            actions.Add(new SeamNextAction(
                "runtime-proof",
                "Lifecycle ownership is still unproven by static evidence.",
                resolved.QualifiedName,
                true));
        }

        return actions
            .GroupBy(action => action.Kind, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(action => ActionOrder(action.Kind))
            .ThenBy(action => action.Scope, StringComparer.Ordinal)
            .ToArray();
    }

    private static SeamConclusion DetermineConclusion(
        SeamOwnerCandidate? selectedCandidate,
        IReadOnlyList<SeamOwnerCandidate> ownerCandidates,
        int ownerCandidateTotalCount,
        EvidenceCoverage ownerCandidateCoverage,
        IReadOnlyList<string> coverageWarnings,
        IReadOnlyList<string> unknownDimensions,
        EvidenceCoverage bodyCoverage,
        EvidenceCoverage callableCoverage,
        IReadOnlyList<SeamEvidenceSection> sections,
        EvidenceCoverage lifecycleCoverage,
        EvidenceCoverage apiCoverage)
    {
        var incompleteEvidence = bodyCoverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable ||
            callableCoverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable ||
            ownerCandidateCoverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable ||
            lifecycleCoverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable ||
            apiCoverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable ||
            sections.Any(section => section.Coverage is EvidenceCoverage.Incomplete or EvidenceCoverage.Unavailable);
        if (incompleteEvidence)
            return SeamConclusion.InsufficientCoverage;

        if (selectedCandidate is not null &&
            ownerCandidates.Count == 1 &&
            ownerCandidateTotalCount == 1 &&
            ownerCandidateCoverage == EvidenceCoverage.Complete &&
            coverageWarnings.Count == 0 &&
            unknownDimensions.Count == 0 &&
            selectedCandidate.Role != "unknown")
            return SeamConclusion.SupportableSeam;
        return SeamConclusion.NoSupportableSeam;
    }

    private static SymbolQueryResult ToSourceSymbol(
        RelationshipEndpointQueryResult endpoint,
        SymbolQueryResult resolved,
        CanonicalKeyCatalog canonicalKeys)
    {
        var isReference = string.Equals(endpoint.Origin, "reference", StringComparison.Ordinal) ||
                          !string.IsNullOrWhiteSpace(endpoint.Collection) ||
                          !string.IsNullOrWhiteSpace(endpoint.ReferenceModId);
        var isGame = string.Equals(endpoint.Origin, "game", StringComparison.Ordinal);
        var indexId = isReference
            ? canonicalKeys.ReferenceIndexId ?? resolved.IndexId
            : isGame
                ? canonicalKeys.GameIndexId ?? resolved.IndexId
                : resolved.IndexId;
        var codebase = isReference
            ? CodebaseKind.ReferenceMod.ToString()
            : isGame
                ? CodebaseKind.ScheduleI.ToString()
                : resolved.Codebase;
        var channel = isReference || isGame ? CodeChannel.Installed.ToString() : resolved.Channel;
        return new SymbolQueryResult(
            indexId,
            codebase,
            channel,
            endpoint.SymbolId ?? string.Empty,
            InferSymbolKind(endpoint),
            endpoint.QualifiedName ?? endpoint.Signature ?? endpoint.SymbolId ?? string.Empty,
            endpoint.Signature ?? endpoint.QualifiedName ?? endpoint.SymbolId ?? string.Empty,
            false,
            endpoint.Origin,
            endpoint.Collection,
            endpoint.ReferenceModId,
            endpoint.DisplayName,
            endpoint.Version,
            endpoint.License,
            endpoint.RelativePath,
            endpoint.Sha256);
    }

    private static string InferSymbolKind(RelationshipEndpointQueryResult endpoint)
    {
        if (endpoint.Signature?.Contains("::.ctor(", StringComparison.Ordinal) == true)
            return SymbolKind.Constructor.ToString();
        return SymbolKind.Method.ToString();
    }

    private static string ClassifyRole(SymbolQueryResult symbol)
    {
        var text = (symbol.QualifiedName + " " + symbol.Signature).ToLowerInvariant();
        if (text.Contains("request", StringComparison.Ordinal))
            return "request";
        if (text.Contains("rpc", StringComparison.Ordinal))
            return "rpc-ingress";
        if (text.Contains("server", StringComparison.Ordinal) || text.Contains("host", StringComparison.Ordinal))
        {
            if (text.Contains("release", StringComparison.Ordinal) || text.Contains("cleanup", StringComparison.Ordinal) || text.Contains("dispose", StringComparison.Ordinal))
                return "cleanup";
            return "host-logic";
        }
        if (text.Contains("write", StringComparison.Ordinal) || text.Contains("apply", StringComparison.Ordinal))
            return "state-writer";
        if (text.Contains("event", StringComparison.Ordinal) || text.Contains("emit", StringComparison.Ordinal))
            return "event-emission";
        if (text.Contains("ui", StringComparison.Ordinal) || text.Contains("panel", StringComparison.Ordinal) || text.Contains("view", StringComparison.Ordinal) || text.Contains("present", StringComparison.Ordinal))
            return "presentation";
        if (text.Contains("save", StringComparison.Ordinal) || text.Contains("store", StringComparison.Ordinal) || text.Contains("persist", StringComparison.Ordinal) || text.Contains("repository", StringComparison.Ordinal))
            return "persistence";
        if (text.Contains("release", StringComparison.Ordinal) || text.Contains("cleanup", StringComparison.Ordinal) || text.Contains("dispose", StringComparison.Ordinal))
            return "cleanup";
        return "unknown";
    }

    private static bool IsOwnerCandidateKind(string kind) =>
        string.Equals(kind, SymbolKind.Method.ToString(), StringComparison.Ordinal) ||
        string.Equals(kind, SymbolKind.Constructor.ToString(), StringComparison.Ordinal);

    private static bool IsCallable(string kind) =>
        string.Equals(kind, SymbolKind.Method.ToString(), StringComparison.Ordinal) ||
        string.Equals(kind, SymbolKind.Constructor.ToString(), StringComparison.Ordinal);

    private static bool IsGameSymbol(SymbolQueryResult symbol) =>
        string.Equals(symbol.Codebase, CodebaseKind.ScheduleI.ToString(), StringComparison.Ordinal) &&
        string.Equals(symbol.Channel, CodeChannel.Installed.ToString(), StringComparison.Ordinal);

    private static IndexRunRecord PinnedRun(SymbolQueryResult symbol) =>
        new(symbol.IndexId, string.Empty, IndexRunStatus.Completed, string.Empty);

    private static int ActionOrder(string kind) =>
        kind switch
        {
            "qualify-symbol" => 0,
            "api-lookup" => 1,
            "targeted-native-recovery" => 2,
            "runtime-proof" => 3,
            _ => 4
        };

    private readonly record struct TraversalKey(string Origin, string IndexId, string SymbolId)
    {
        public static TraversalKey For(SymbolQueryResult symbol) =>
            new(symbol.Origin ?? string.Empty, symbol.IndexId, symbol.SymbolId);
    }

    private sealed record TraversalState(TraversalKey Key, int Depth);

    private sealed record IncomingTraversalResult(
        IReadOnlyList<RelationshipQueryResult> Relationships,
        EvidenceCoverage Coverage);

    private sealed record OwnerCandidateSet(
        IReadOnlyList<SeamOwnerCandidate> Candidates,
        int TotalCount,
        EvidenceCoverage Coverage);

    private sealed record CanonicalKeyCatalog(
        IReadOnlyDictionary<string, string> Keys,
        string ResolvedIndexId,
        string? GameIndexId,
        string? ReferenceIndexId)
    {
        public bool TryGetValue(SymbolQueryResult symbol, out string canonicalKey)
        {
            canonicalKey = string.Empty;
            return Keys.TryGetValue(symbol.IndexId + "\u001f" + symbol.SymbolId, out canonicalKey!);
        }
    }

    private sealed record TraversalPath(
        SymbolQueryResult Symbol,
        string CanonicalKey,
        IReadOnlyList<string> RelationshipIds,
        IReadOnlyList<string> RelationshipFamilies)
    {
        public int PathLength => RelationshipIds.Count;

        public int SupportingRelationshipFamilyCount => RelationshipFamilies
            .Distinct(StringComparer.Ordinal)
            .Count();

        public TraversalPath Prepend(SymbolQueryResult symbol, string canonicalKey, RelationshipQueryResult edge)
        {
            var ids = new string[RelationshipIds.Count + 1];
            ids[0] = edge.RelationshipId;
            for (var i = 0; i < RelationshipIds.Count; i++)
            {
                ids[i + 1] = RelationshipIds[i];
            }

            var families = new string[RelationshipFamilies.Count + 1];
            families[0] = edge.Kind;
            for (var i = 0; i < RelationshipFamilies.Count; i++)
            {
                families[i + 1] = RelationshipFamilies[i];
            }

            return new TraversalPath(symbol, canonicalKey, ids, families);
        }

        public bool IsBetterThan(TraversalPath other)
        {
            if (PathLength != other.PathLength)
                return PathLength < other.PathLength;

            var current = string.Join("|", RelationshipIds);
            var existing = string.Join("|", other.RelationshipIds);
            return string.CompareOrdinal(current, existing) < 0;
        }

        public static TraversalPath Start(SymbolQueryResult symbol, string canonicalKey, RelationshipQueryResult edge) =>
            new(symbol, canonicalKey, [edge.RelationshipId], [edge.Kind]);
    }
}
