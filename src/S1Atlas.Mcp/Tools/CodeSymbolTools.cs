using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSymbolTools
{
    private static readonly HashSet<string> RelatedTypeRelationshipKinds = new(
        [
            "Inherits",
            "ImplementsInterface",
            "FieldType",
            "PropertyType",
            "EventType",
            "ParameterType",
            "ReturnType"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly McpReadOnlyServices _services;

    public CodeSymbolTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "search_symbols"), Description("Search the integrity-verified Schedule I game index or an explicitly selected local reference collection for symbols.")]
    public async Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Optional symbol kind filter.")] string? kind = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(query, authority, out ToolEnvelope<SymbolSearchResult> queryError))
                {
                    return queryError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<SymbolSearchResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryParseKind(kind, authority, out var parsedKind, out ToolEnvelope<SymbolSearchResult> kindError))
                {
                    return kindError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<SymbolSearchResult> scopeError))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<SymbolSearchResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.SearchInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        query,
                        boundedLimit,
                        parsedKind,
                        ct)
                    : await _services.FederatedIndexQueryService.SearchAsync(
                        query,
                        options,
                        ct,
                        parsedKind);
                return EnvelopeMapper.FromScopedSearch(authority, result, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "get_type"), Description("Resolve one type from the preferred, integrity-verified Schedule I code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetTypeAsync(
        [Description("Exact or fuzzy type selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max candidates (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, buildId, SymbolKind.Type, limit, ct);

    [McpServerTool(Name = "get_method"), Description("Resolve one method from the preferred, integrity-verified Schedule I code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetMethodAsync(
        [Description("Exact or fuzzy method selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max candidates (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, buildId, SymbolKind.Method, limit, ct);

    [McpServerTool(Name = "get_callable_surface"), Description("Resolve how one Schedule I game member is callable through its local Il2CppInterop projection.")]
    public async Task<ToolEnvelope<CallableSurfaceQueryResult>> GetCallableSurfaceAsync(
        [Description("Exact or fuzzy game-member selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        CancellationToken ct = default)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<CallableSurfaceQueryResult> selectorError))
                    return selectorError;

                var result = await _services.IndexQueryService.GetCallableSurfaceInIndexAsync(
                    authority.IndexRun!,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    selector,
                    ct);
                return EnvelopeMapper.FromCallableSurface(authority, result);
            });
    }

    [McpServerTool(Name = "get_source"), Description("Return integrity-checked source for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Source context lines before and after the selected span.")] int context = 5,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null,
        [Description("Return the containing type's verified source span.")] bool fullType = false,
        [Description("Max caller/callee neighborhood rows per direction (0-50). Zero disables neighborhood lookup.")] int relatedLimit = 10)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<SourceSnippetQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundContext(context, authority, out var boundedContext, out ToolEnvelope<SourceSnippetQueryResult> contextError))
                {
                    return contextError;
                }

                if (!ToolArguments.TryBoundRelatedLimit(relatedLimit, authority, out var boundedRelatedLimit, out ToolEnvelope<SourceSnippetQueryResult> relatedLimitError))
                {
                    return relatedLimitError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<SourceSnippetQueryResult> scopeError))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<SourceSnippetQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;

                try
                {
                    var result = options.Scope == IndexQueryScope.Game
                        ? await _services.IndexQueryService.SourceInIndexAsync(
                            authority.IndexRun!,
                            CodebaseKind.ScheduleI,
                            CodeChannel.Installed,
                            selector,
                            boundedContext,
                            ct,
                            fullType,
                            boundedRelatedLimit)
                        : await _services.FederatedIndexQueryService.SourceAsync(
                            selector,
                            options,
                            boundedContext,
                            ct,
                            fullType,
                            boundedRelatedLimit,
                            pinned.ReferenceCollection?.ReferenceIndexId);
                    return EnvelopeMapper.FromScopedSource(authority, result, options.ReferenceCollection);
                }
                catch (InvalidDataException)
                {
                    return EnvelopeMapper.SourceIntegrityFailure<SourceSnippetQueryResult>(authority);
                }
                catch (FileNotFoundException)
                {
                    return EnvelopeMapper.SourceUnavailable<SourceSnippetQueryResult>(authority);
                }
            });
    }

    [McpServerTool(Name = "find_callers"), Description("Find incoming call-like relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCallersAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null) =>
        await FindRelationshipsAsync(
            selector,
            buildId,
            limit,
            ct,
            scope,
            collection,
            RelationshipDirection.Callers);

    [McpServerTool(Name = "find_callees"), Description("Find outgoing call-like relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCalleesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null) =>
        await FindRelationshipsAsync(selector, buildId, limit, ct, scope, collection, RelationshipDirection.Callees);

    [McpServerTool(Name = "find_references"), Description("Find incoming and outgoing relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindReferencesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null) =>
        await FindRelationshipsAsync(
            selector,
            buildId,
            limit,
            ct,
            scope,
            collection,
            RelationshipDirection.References);

    [McpServerTool(Name = "find_call_sites"), Description("Find recovered-IL static call-site references for a game member or canonical raw target text; results do not prove runtime behavior or call order.")]
    public async Task<ToolEnvelope<CallSiteQueryResult>> FindCallSitesAsync(
        [Description("Resolved game-member selector or canonical raw target text.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<CallSiteQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<CallSiteQueryResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<CallSiteQueryResult> scopeError))
                {
                    return scopeError;
                }

                var pinned = await PinAuthorityAsync<CallSiteQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.CallSitesInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        ct)
                    : await _services.FederatedIndexQueryService.CallSitesAsync(
                        selector,
                        options,
                        ct,
                        pinned.ReferenceCollection?.ReferenceIndexId);
                return EnvelopeMapper.FromScopedCallSites(authority, result, options.ReferenceCollection, pinned.ReferenceCollection?.ReferenceIndexId);
            });
    }

    [McpServerTool(Name = "find_field_references"), Description("Find recovered-IL static field readers and writers for one resolved game or local reference field; results do not prove lifecycle ordering or runtime behavior.")]
    public async Task<ToolEnvelope<FieldReferenceQueryResult>> FindFieldReferencesAsync(
        [Description("Exact or fuzzy field selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Return only field readers.")] bool readers = false,
        [Description("Return only field writers.")] bool writers = false,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<FieldReferenceQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryParseFieldReferenceFilter(
                        readers,
                        writers,
                        authority,
                        out var filter,
                        out ToolEnvelope<FieldReferenceQueryResult> filterError))
                {
                    return filterError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<FieldReferenceQueryResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<FieldReferenceQueryResult> scopeError))
                {
                    return scopeError;
                }

                var pinned = await PinAuthorityAsync<FieldReferenceQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.FieldReferencesInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        filter,
                        ct)
                    : await _services.FederatedIndexQueryService.FieldReferencesAsync(
                        selector,
                        options,
                        filter,
                        ct,
                        pinned.ReferenceCollection?.ReferenceIndexId);
                return EnvelopeMapper.FromScopedFieldReferences(authority, result, options.ReferenceCollection, pinned.ReferenceCollection?.ReferenceIndexId);
            });
    }

    [McpServerTool(Name = "find_related_types"), Description("Find type-oriented relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelatedTypesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Optional type relationship kinds to include.")] string[]? relationKinds = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Optional scope: game (default), reference, or all.")] string? scope = null,
        [Description("Required for reference or all scope; accepts a collection ID or completed reference index ID.")] string? collection = null)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<RelationshipQuerySetResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<RelationshipQuerySetResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryParseRelationshipKinds(
                        relationKinds,
                        authority,
                        out var selectedKinds,
                        out ToolEnvelope<RelationshipQuerySetResult> kindError))
                {
                    return kindError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<RelationshipQuerySetResult> scopeError))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<RelationshipQuerySetResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.RefsInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        500,
                        ct)
                    : await _services.FederatedIndexQueryService.RefsAsync(
                        selector,
                        options with { Limit = 500 },
                        ct);
                if (result.Resolution.Status == SymbolResolutionStatus.Resolved)
                {
                    result = result with
                    {
                        Relationships = result.Relationships
                            .Where(edge => selectedKinds.Contains(edge.Kind))
                            .Take(boundedLimit)
                            .ToArray()
                    };
                }

                return EnvelopeMapper.FromScopedRelationships(authority, result, options.ReferenceCollection);
            });
    }

    private async Task<ToolEnvelope<SymbolQueryResult>> GetSymbolAsync(
        string selector,
        string? buildId,
        SymbolKind kind,
        int limit,
        CancellationToken ct)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<SymbolQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<SymbolQueryResult> limitError))
                {
                    return limitError;
                }

                var result = await _services.IndexQueryService.SearchInIndexAsync(
                    authority.IndexRun!,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    selector,
                    boundedLimit,
                    kind,
                    ct);
                return EnvelopeMapper.FromResolveOne(authority, result);
            });
    }

    private async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelationshipsAsync(
        string selector,
        string? buildId,
        int limit,
        CancellationToken ct,
        string? scope,
        string? collection,
        RelationshipDirection direction)
    {
        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<RelationshipQuerySetResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<RelationshipQuerySetResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<RelationshipQuerySetResult> scopeError))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<RelationshipQuerySetResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? direction switch
                    {
                        RelationshipDirection.Callers => await _services.IndexQueryService.CallersInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct),
                        RelationshipDirection.Callees => await _services.IndexQueryService.CalleesInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct),
                        _ => await _services.IndexQueryService.RefsInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct)
                    }
                    : direction switch
                    {
                        RelationshipDirection.Callers => await _services.FederatedIndexQueryService.CallersAsync(selector, options, ct),
                        RelationshipDirection.Callees => await _services.FederatedIndexQueryService.CalleesAsync(selector, options, ct),
                        _ => await _services.FederatedIndexQueryService.RefsAsync(selector, options, ct)
                    };
                return EnvelopeMapper.FromScopedRelationships(authority, result, options.ReferenceCollection);
            });
    }

    private enum RelationshipDirection { References, Callers, Callees }

    private async Task<ScopedAuthority<T>> PinAuthorityAsync<T>(
        S1Atlas.Application.Authority.InstalledBuildAuthority authority,
        string? requestedBuildId,
        IndexQueryOptions options,
        CancellationToken ct) where T : class
    {
        if (options.Scope == IndexQueryScope.Game)
            return new(authority, null);

        var collection = await _services.ReferenceModQueryService.GetCollectionAuthorityAsync(
            options.ReferenceCollection!,
            ct);
        if (collection is null)
        {
            return new(
                authority,
                ToolEnvelope<T>.NotFound(
                    EnvelopeMapper.BuildFrom(authority),
                    new ToolError("NoCompletedIndex", "No completed reference collection exists for the requested scope."),
                    new ProvenanceEntry(ProvenanceClassification.Derived, "reference-collection-selection", null, null, null)));
        }

        var baseAuthority = await _services.AuthorityResolver.ResolveAsync(collection.BuildId, ct);
        if (baseAuthority.Status != S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.Resolved)
            return new(baseAuthority, AuthorityEnvelope.From<T>(baseAuthority));

        if (!string.IsNullOrWhiteSpace(requestedBuildId) &&
            !string.Equals(requestedBuildId, collection.BuildId, StringComparison.Ordinal))
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
                        collection.BuildId,
                        baseAuthority.ExtractionId,
                        collection.BaseIndexId)));
        }

        if (!string.Equals(baseAuthority.IndexId, collection.BaseIndexId, StringComparison.Ordinal))
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
                        collection.BuildId,
                        baseAuthority.ExtractionId,
                        collection.BaseIndexId)));
        }

        return new(baseAuthority, null, collection);
    }

    private sealed record ScopedAuthority<T>(
        S1Atlas.Application.Authority.InstalledBuildAuthority Authority,
        ToolEnvelope<T>? Error,
        ReferenceCollectionAuthorityQueryResult? ReferenceCollection = null) where T : class;

    private static class ToolArguments
    {
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
                "InvalidArguments",
                "The selector must not be blank or whitespace.");
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

        public static bool TryBoundRelatedLimit<T>(
            int relatedLimit,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (relatedLimit is < 0 or > 50)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidRelatedLimit", "The related result limit must be between 0 and 50.");
                return false;
            }

            bounded = relatedLimit;
            error = null!;
            return true;
        }

        public static bool TryBoundLimit<T>(
            int limit,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (limit <= 0)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidLimit", "The query result limit must be positive.");
                return false;
            }

            bounded = Math.Min(limit, 500);
            error = null!;
            return true;
        }

        public static bool TryParseKind<T>(
            string? kind,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out SymbolKind? parsed,
            out ToolEnvelope<T> error) where T : class
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                parsed = null;
                error = null!;
                return true;
            }

            if (Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var parsedKind))
            {
                parsed = parsedKind;
                error = null!;
                return true;
            }

            parsed = null;
            error = Invalid<T>(
                authority,
                "InvalidKind",
                "Symbol kind must be Type, Constructor, Method, Field, Property, or Event.");
            return false;
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

        public static bool TryParseRelationshipKinds<T>(
            string[]? relationKinds,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out IReadOnlySet<string> parsed,
            out ToolEnvelope<T> error) where T : class
        {
            if (relationKinds is null)
            {
                parsed = RelatedTypeRelationshipKinds;
                error = null!;
                return true;
            }

            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in relationKinds)
            {
                if (string.IsNullOrWhiteSpace(kind) || !RelatedTypeRelationshipKinds.Contains(kind))
                {
                    parsed = null!;
                    error = Invalid<T>(
                        authority,
                        "InvalidKind",
                        "Relation kinds must be type-oriented relationship kinds.");
                    return false;
                }

                selected.Add(kind);
            }

            parsed = selected;
            error = null!;
            return true;
        }

        public static bool TryParseFieldReferenceFilter<T>(
            bool readers,
            bool writers,
            S1Atlas.Application.Authority.InstalledBuildAuthority authority,
            out FieldReferenceFilter filter,
            out ToolEnvelope<T> error) where T : class
        {
            if (readers && writers)
            {
                filter = default;
                error = Invalid<T>(
                    authority,
                    "InvalidOptionCombination",
                    "--readers and --writers are mutually exclusive.");
                return false;
            }

            filter = readers
                ? FieldReferenceFilter.Readers
                : writers
                    ? FieldReferenceFilter.Writers
                    : FieldReferenceFilter.All;
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
