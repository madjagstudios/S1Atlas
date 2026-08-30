using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Mcp.Mapping;

public static class EnvelopeMapper
{
    public static async Task<ToolEnvelope<T>> WithAuthorityAsync<T>(
        InstalledBuildAuthorityResolver resolver,
        string? buildId,
        CancellationToken ct,
        Func<InstalledBuildAuthority, Task<ToolEnvelope<T>>> onResolved) where T : class
    {
        InstalledBuildAuthority authority;
        try
        {
            authority = await resolver.ResolveAsync(buildId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            return ToolEnvelope<T>.Unavailable(
                new ToolError("AtlasUnavailable", "The Atlas data store is unavailable."));
        }

        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return AuthorityEnvelope.From<T>(authority);
        }

        try
        {
            return await onResolved(authority);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            return ToolEnvelope<T>.Unavailable(
                new ToolError("UnexpectedToolFailure", "The MCP tool could not complete."),
                BuildFrom(authority));
        }
    }

    public static async Task<ToolEnvelope<T>> WithAtlasAvailabilityAsync<T>(
        Func<Task<ToolEnvelope<T>>> operation) where T : class
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            return ToolEnvelope<T>.Unavailable(
                new ToolError("AtlasUnavailable", "The Atlas data store is unavailable."));
        }
    }

    public static ToolEnvelope<SymbolQueryResult> FromResolveOne(
        InstalledBuildAuthority authority,
        SymbolSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var build = BuildFrom(authority);
        return result.TotalCount switch
        {
            0 => ToolEnvelope<SymbolQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            1 => ToolEnvelope<SymbolQueryResult>.Resolved(
                build,
                result.Results[0],
                Fact(authority, "index-symbol"),
                Derived(authority, "symbol-selection")),
            _ => ToolEnvelope<SymbolQueryResult>.Ambiguous(
                build,
                result.Results.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection"))
        };
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

    public static ToolEnvelope<ApiIndexCatalogResult> FromApiCatalog(ApiIndexCatalogResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var provenance = result.Selections
            .Where(selection => selection.Availability == ApiIndexAvailability.Current)
            .Select(selection => ApiFact(selection, result.ResolvedBuildId))
            .ToArray();
        return ToolEnvelope<ApiIndexCatalogResult>.Resolved(
            build: null,
            result,
            provenance);
    }

    public static ToolEnvelope<T> FromApiSelectionFailure<T>(
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

    public static ToolEnvelope<SymbolSearchResult> FromApiSearch(
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

    public static ToolEnvelope<SourceSnippetQueryResult> FromApiSource(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        SourceSnippetResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<SourceSnippetQueryResult>.Ambiguous(
                build,
                result.Resolution.Candidates.Cast<object>().ToArray(),
                provenance),
            SymbolResolutionStatus.NotFound => ToolEnvelope<SourceSnippetQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed API symbol matched the selector."),
                provenance),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<SourceSnippetQueryResult>.NotFound(
                build,
                new ToolError("NoCompletedIndex", "No completed API index exists for the requested scope."),
                provenance),
            _ when result.Snippet is null => ToolEnvelope<SourceSnippetQueryResult>.Unavailable(
                new ToolError("SourceUnavailable", "The selected API symbol has no integrity-checked source location."),
                build,
                provenance),
            _ => ToolEnvelope<SourceSnippetQueryResult>.Resolved(build, result.Snippet, provenance)
        };
    }

    public static ToolEnvelope<SourceSnippetQueryResult> ApiSourceIntegrityFailure(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection) =>
        ToolEnvelope<SourceSnippetQueryResult>.Unavailable(
            new ToolError("SourceIntegrityFailure", "The completed API source failed integrity verification."),
            ApiBuildFrom(catalog, selection),
            ApiProvenance(catalog, selection));

    public static ToolEnvelope<SourceSnippetQueryResult> ApiSourceUnavailable(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection) =>
        ToolEnvelope<SourceSnippetQueryResult>.Unavailable(
            new ToolError("SourceUnavailable", "The completed API source is unavailable."),
            ApiBuildFrom(catalog, selection),
            ApiProvenance(catalog, selection));

    public static ToolEnvelope<RelationshipQuerySetResult> FromApiRelationships(
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

    public static ToolEnvelope<CallSiteQueryResult> FromApiCallSites(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        CallSiteQueryResult result) =>
        ToolEnvelope<CallSiteQueryResult>.Resolved(
            ApiBuildFrom(catalog, selection),
            result,
            ApiProvenance(catalog, selection));

    public static ToolEnvelope<FieldReferenceQueryResult> FromApiFieldReferences(
        ApiIndexCatalogResult catalog,
        ApiIndexSelection selection,
        FieldReferenceQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var build = ApiBuildFrom(catalog, selection);
        var provenance = ApiProvenance(catalog, selection);
        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<FieldReferenceQueryResult>.Ambiguous(
                build,
                result.Resolution.Candidates.Cast<object>().ToArray(),
                provenance),
            SymbolResolutionStatus.NotFound => ToolEnvelope<FieldReferenceQueryResult>.NotFound(
                build,
                new ToolError("SymbolNotFound", "No indexed API symbol matched the selector."),
                provenance),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<FieldReferenceQueryResult>.NotFound(
                build,
                new ToolError("NoCompletedIndex", "No completed API index exists for the requested scope."),
                provenance),
            _ => ToolEnvelope<FieldReferenceQueryResult>.Resolved(build, result, provenance)
        };
    }

    public static ToolEnvelope<SymbolSearchResult> FromScopedSearch(
        InstalledBuildAuthority authority,
        SymbolSearchResult result,
        string? collection)
    {
        var envelope = result.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex
            ? ToolEnvelope<SymbolSearchResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed index exists for the requested scope."),
                Derived(authority, "index-selection"))
            : FromSearch(authority, result);
        return AddReferenceProvenance(envelope, authority, collection, result.Results);
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

    public static ToolEnvelope<SourceSnippetQueryResult> FromScopedSource(
        InstalledBuildAuthority authority,
        SourceSnippetResolutionResult result,
        string? collection) =>
        AddReferenceProvenance(
            FromSource(authority, result),
            authority,
            collection,
            result.Resolution.Symbol is { } symbol ? [symbol] : result.Resolution.Candidates);

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

    public static ToolEnvelope<RelationshipQuerySetResult> FromScopedRelationships(
        InstalledBuildAuthority authority,
        RelationshipQuerySetResult result,
        string? collection) =>
        AddReferenceProvenance(
            FromRelationships(authority, result),
            authority,
            collection,
            (result.Resolution.Symbol is { } symbol ? new[] { symbol } : result.Resolution.Candidates)
                .Concat(result.Relationships.SelectMany(edge => new[] { edge.Source, edge.Target })
                .Where(endpoint => endpoint.Origin == "reference")
                .Select(endpoint => new SymbolQueryResult(
                    string.Empty,
                    "ReferenceMod",
                    "Installed",
                    endpoint.SymbolId ?? string.Empty,
                    string.Empty,
                    endpoint.QualifiedName ?? string.Empty,
                    endpoint.Signature ?? string.Empty,
                    false,
                    endpoint.Origin,
                    endpoint.Collection,
                    endpoint.ReferenceModId,
                    endpoint.DisplayName,
                    endpoint.Version,
                    endpoint.License,
                    endpoint.RelativePath,
                    endpoint.Sha256))));

    public static ToolEnvelope<CallSiteQueryResult> FromCallSites(
        InstalledBuildAuthority authority,
        CallSiteQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return ToolEnvelope<CallSiteQueryResult>.Resolved(
            BuildFrom(authority),
            result,
            Fact(authority, "call-site-query"),
            Derived(authority, "relationship-direction"));
    }

    public static ToolEnvelope<CallSiteQueryResult> FromScopedCallSites(
        InstalledBuildAuthority authority,
        CallSiteQueryResult result,
        string? collection,
        string? referenceIndexId) =>
        AddReferenceCollectionProvenance(
            FromCallSites(authority, result),
            authority,
            collection,
            referenceIndexId);

    public static ToolEnvelope<FieldReferenceQueryResult> FromFieldReferences(
        InstalledBuildAuthority authority,
        FieldReferenceQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<FieldReferenceQueryResult>.Ambiguous(
                BuildFrom(authority),
                result.Resolution.Candidates.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NotFound => ToolEnvelope<FieldReferenceQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<FieldReferenceQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."),
                Derived(authority, "symbol-selection")),
            _ => ToolEnvelope<FieldReferenceQueryResult>.Resolved(
                BuildFrom(authority),
                result,
                Fact(authority, "field-reference-query"),
                Derived(authority, "relationship-direction"))
        };
    }

    public static ToolEnvelope<FieldReferenceQueryResult> FromScopedFieldReferences(
        InstalledBuildAuthority authority,
        FieldReferenceQueryResult result,
        string? collection,
        string? referenceIndexId) =>
        AddReferenceCollectionProvenance(
            FromFieldReferences(authority, result),
            authority,
            collection,
            referenceIndexId);

    public static ToolEnvelope<CallableSurfaceQueryResult> FromCallableSurface(
        InstalledBuildAuthority authority,
        CallableSurfaceResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<CallableSurfaceQueryResult>.Ambiguous(
                BuildFrom(authority),
                result.Resolution.Candidates.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NotFound => ToolEnvelope<CallableSurfaceQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<CallableSurfaceQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."),
                Derived(authority, "symbol-selection")),
            _ when result.CallableSurface is null => ToolEnvelope<CallableSurfaceQueryResult>.NotFound(
                BuildFrom(authority),
                new ToolError("CallableSurfaceUnavailable", "The selected symbol has no callable-surface result."),
                Derived(authority, "callable-surface-selection")),
            _ => ToolEnvelope<CallableSurfaceQueryResult>.Resolved(
                BuildFrom(authority),
                result.CallableSurface,
                Fact(authority, "index-callable-surface"),
                Derived(authority, "callable-surface-selection"))
        };
    }

    public static ToolEnvelope<SeamInvestigationResult> FromSeamInvestigation(
        InstalledBuildAuthority authority,
        SeamInvestigationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => ToolEnvelope<SeamInvestigationResult>.Ambiguous(
                BuildFrom(authority),
                result.Resolution.Candidates.Cast<object>().ToArray(),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NotFound => ToolEnvelope<SeamInvestigationResult>.NotFound(
                BuildFrom(authority),
                new ToolError("SymbolNotFound", "No indexed symbol matched the selector."),
                Derived(authority, "symbol-selection")),
            SymbolResolutionStatus.NoCompletedIndex => ToolEnvelope<SeamInvestigationResult>.NotFound(
                BuildFrom(authority),
                new ToolError("NoCompletedIndex", "No completed Schedule I Installed index exists for the verified extraction."),
                Derived(authority, "symbol-selection")),
            _ when !HasRequiredSeamGateRecords(result) => ToolEnvelope<SeamInvestigationResult>.Unavailable(
                new ToolError("IncompleteSeamResult", "The resolved seam result is missing required gate records."),
                BuildFrom(authority),
                Fact(authority, "seam-investigation"),
                Derived(authority, "seam-evaluation")),
            _ => ToolEnvelope<SeamInvestigationResult>.Resolved(
                BuildFrom(authority),
                result,
                Fact(authority, "seam-investigation"),
                Derived(authority, "seam-evaluation"))
        };
    }

    public static ToolEnvelope<SeamInvestigationResult> FromScopedSeamInvestigation(
        InstalledBuildAuthority authority,
        SeamInvestigationResult result,
        string? collection,
        ReferenceCollectionAuthorityQueryResult? referenceAuthority,
        IndexQueryScope scope)
    {
        var mapped = FromSeamInvestigation(
            authority,
            result with
            {
                PinnedProvenance = BuildSeamPinnedProvenance(authority, result, referenceAuthority, scope)
            });
        return AddSeamReferenceProvenance(mapped, authority, collection, referenceAuthority, result, scope);
    }

    public static ToolEnvelope<T> Invalid<T>(string code, string message) where T : class =>
        ToolEnvelope<T>.Invalid(new ToolError(code, message));

    public static ToolEnvelope<T> SourceIntegrityFailure<T>(
        InstalledBuildAuthority authority) where T : class =>
        ToolEnvelope<T>.Unavailable(
            new ToolError("SourceIntegrityFailure", "The indexed source failed integrity verification."),
            BuildFrom(authority),
            Derived(authority, "source-integrity"));

    public static ToolEnvelope<T> SourceUnavailable<T>(
        InstalledBuildAuthority authority) where T : class =>
        ToolEnvelope<T>.Unavailable(
            new ToolError("SourceUnavailable", "The indexed source is unavailable."),
            BuildFrom(authority),
            Derived(authority, "source-selection"));

    private static ToolEnvelope<T> AddReferenceProvenance<T>(
        ToolEnvelope<T> envelope,
        InstalledBuildAuthority authority,
        string? requestedCollection,
        IEnumerable<SymbolQueryResult> symbols) where T : class
    {
        var referenceIndexId = symbols
            .Where(symbol => symbol.Origin == "reference")
            .Select(symbol => symbol.IndexId)
            .FirstOrDefault(indexId => !string.IsNullOrWhiteSpace(indexId));
        return AddReferenceCollectionProvenance(envelope, authority, requestedCollection, referenceIndexId);
    }

    private static ToolEnvelope<T> AddSeamReferenceProvenance<T>(
        ToolEnvelope<T> envelope,
        InstalledBuildAuthority authority,
        string? requestedCollection,
        ReferenceCollectionAuthorityQueryResult? referenceAuthority,
        SeamInvestigationResult result,
        IndexQueryScope scope) where T : class
    {
        var selected = SelectSeamAuthority(authority, referenceAuthority, result, scope);
        var selectedEnvelope = envelope with
        {
            Provenance = envelope.Provenance
                .Select(entry => new ProvenanceEntry(
                    entry.Classification,
                    entry.Source,
                    selected.BuildId,
                    selected.ExtractionId,
                    selected.IndexId))
                .ToArray()
        };
        return AddSeamReferenceCollectionProvenance(
            selectedEnvelope,
            authority,
            requestedCollection,
            referenceAuthority);
    }

    private static ToolEnvelope<T> AddReferenceCollectionProvenance<T>(
        ToolEnvelope<T> envelope,
        InstalledBuildAuthority authority,
        string? requestedCollection,
        string? referenceIndexId) where T : class
    {
        if (string.IsNullOrWhiteSpace(requestedCollection))
            return envelope;

        var provenance = envelope.Provenance
            .Append(new ProvenanceEntry(
                ProvenanceClassification.Fact,
                "reference-collection",
                authority.ResolvedBuildId,
                authority.ExtractionId,
                referenceIndexId))
            .ToArray();
        return envelope with { Provenance = provenance };
    }

    private static ToolEnvelope<T> AddSeamReferenceCollectionProvenance<T>(
        ToolEnvelope<T> envelope,
        InstalledBuildAuthority authority,
        string? requestedCollection,
        ReferenceCollectionAuthorityQueryResult? referenceAuthority) where T : class
    {
        if (string.IsNullOrWhiteSpace(requestedCollection) || referenceAuthority is null)
            return envelope;

        var provenance = envelope.Provenance
            .Append(new ProvenanceEntry(
                ProvenanceClassification.Fact,
                "reference-collection-base",
                referenceAuthority.BuildId,
                authority.ExtractionId,
                referenceAuthority.BaseIndexId))
            .Append(new ProvenanceEntry(
                ProvenanceClassification.Fact,
                "reference-collection",
                referenceAuthority.BuildId,
                null,
                referenceAuthority.ReferenceIndexId))
            .ToArray();
        return envelope with { Provenance = provenance };
    }

    private static SeamPinnedProvenance BuildSeamPinnedProvenance(
        InstalledBuildAuthority authority,
        SeamInvestigationResult result,
        ReferenceCollectionAuthorityQueryResult? referenceAuthority,
        IndexQueryScope scope)
    {
        var selected = SelectSeamAuthority(authority, referenceAuthority, result, scope);
        return new SeamPinnedProvenance(
            selected.IsReference
                ? authority.RequestedBuildId
                : authority.RequestedBuildId ?? selected.BuildId,
            selected.BuildId,
            selected.ExtractionId,
            selected.IndexId,
            selected.Codebase,
            "Installed",
            selected.IntegrityVerified);
    }

    private static SeamAuthoritySelection SelectSeamAuthority(
        InstalledBuildAuthority authority,
        ReferenceCollectionAuthorityQueryResult? referenceAuthority,
        SeamInvestigationResult result,
        IndexQueryScope scope)
    {
        var useReference = referenceAuthority is not null &&
                           (scope == IndexQueryScope.Reference ||
                            scope == IndexQueryScope.All && IsReferenceSeamResult(result));
        return useReference
            ? new(
                referenceAuthority!.BuildId,
                null,
                referenceAuthority.ReferenceIndexId,
                "ReferenceMod",
                IntegrityVerified: false,
                IsReference: true)
            : new(
                authority.ResolvedBuildId,
                authority.ExtractionId,
                referenceAuthority?.BaseIndexId ?? authority.IndexId,
                "ScheduleI",
                IntegrityVerified: true,
                IsReference: false);
    }

    private static bool IsReferenceSeamResult(SeamInvestigationResult result) =>
        result.Resolution.Symbol?.Origin == "reference" ||
        result.Candidate?.Origin == "reference" ||
        result.OwnerCandidates.Any(candidate => candidate.Symbol.Origin == "reference");

    private static bool HasRequiredSeamGateRecords(SeamInvestigationResult result) =>
        result.PinnedProvenance is not null &&
        result.AuthorityEntityAttribution is not null &&
        result.AlternateGenericCallersAndExclusivity is not null &&
        result.LifecyclePositionAndBeforeAfterState is not null &&
        result.ApiBeforePatchResult is not null;

    private sealed record SeamAuthoritySelection(
        string? BuildId,
        string? ExtractionId,
        string? IndexId,
        string Codebase,
        bool IntegrityVerified,
        bool IsReference);

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

    private static bool IsMissingApiIndex(ApiIndexSelection selection) =>
        selection.Message?.StartsWith("No completed", StringComparison.Ordinal) == true;

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

    private static void LogUnexpected(Exception exception) =>
        Console.Error.WriteLine($"Unexpected MCP tool failure: {exception.GetType().Name}: {exception.Message}");
}
