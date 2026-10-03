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

    private static readonly IReadOnlySet<SymbolKind> TypeKinds = new HashSet<SymbolKind>([SymbolKind.Type]);
    private static readonly IReadOnlySet<SymbolKind> MethodKinds = new HashSet<SymbolKind>([SymbolKind.Method]);

    private readonly McpReadOnlyServices _services;

    public CodeSymbolTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "search_symbols", Title = "Search symbols", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Search the integrity-verified game index or a selected reference collection for symbols.")]
    public async Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Symbol kind filter.")] SymbolKind? kind = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Include compiler-generated members with raw sources.")] bool includeGenerated = false)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<SymbolSearchResult>(query, null, out var queryError))
            {
                return queryError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<SymbolSearchResult> limitError))
            {
                return limitError;
            }

            return await WithApiSelectionAsync<SymbolSearchResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.SearchSelectedAsync(
                        selection,
                        query,
                        boundedLimit,
                        ct,
                        includeGenerated,
                        kind);
                    return EnvelopeMapper.FromApiSearch(catalog, selection, result);
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<SymbolSearchResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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
                        kind,
                        ct,
                        includeGenerated)
                    : await _services.FederatedIndexQueryService.SearchAsync(
                        query,
                        options,
                        ct,
                        kind,
                        includeGenerated);
                return EnvelopeMapper.FromScopedSearch(authority, result, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "get_type", Title = "Get type", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one type from the integrity-verified code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetTypeAsync(
        [Description("Exact or fuzzy type selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max candidates (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, codebase, channel, buildId, TypeKinds, limit, ct);

    [McpServerTool(Name = "get_method", Title = "Get method", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve one method from the integrity-verified code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetMethodAsync(
        [Description("Exact or fuzzy method selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max candidates (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, codebase, channel, buildId, MethodKinds, limit, ct);

    [McpServerTool(Name = "get_callable_surface", Title = "Get callable surface", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Resolve how one game member is callable through its local Il2CppInterop projection.")]
    public async Task<ToolEnvelope<CallableSurfaceQueryResult>> GetCallableSurfaceAsync(
        [Description("Exact or fuzzy game-member selector.")] string selector,
        [Description("Build ID; omit for current.")] string? buildId = null,
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

    [McpServerTool(Name = "get_source", Title = "Get source", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Return integrity-checked source for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Source context lines before and after the selected span.")] int context = 5,
        CancellationToken ct = default,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Return the containing type's verified source span.")] bool fullType = false,
        [Description("Neighborhood rows per direction (0-50); 0 disables.")] int relatedLimit = 10)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<SourceSnippetQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundContext(context, null, out var boundedContext, out ToolEnvelope<SourceSnippetQueryResult> contextError))
            {
                return contextError;
            }

            if (!ToolArguments.TryBoundRelatedLimit(relatedLimit, null, out var boundedRelatedLimit, out ToolEnvelope<SourceSnippetQueryResult> relatedLimitError))
            {
                return relatedLimitError;
            }

            return await WithApiSelectionAsync<SourceSnippetQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    try
                    {
                        var result = await _services.ApiIndexQueryService.SourceSelectedAsync(
                            selection,
                            selector,
                            boundedContext,
                            boundedRelatedLimit,
                            ct,
                            fullType);
                        return EnvelopeMapper.FromApiSource(catalog, selection, result);
                    }
                    catch (InvalidDataException)
                    {
                        return EnvelopeMapper.ApiSourceIntegrityFailure(catalog, selection);
                    }
                    catch (FileNotFoundException)
                    {
                        return EnvelopeMapper.ApiSourceUnavailable(catalog, selection);
                    }
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<SourceSnippetQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

    [McpServerTool(Name = "find_callers", Title = "Find callers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find incoming call-like relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCallersAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Only exact callers; omit may-dispatch ones.")] bool exact = false,
        [Description("Include compiler-generated members with raw sources.")] bool includeGenerated = false,
        [Description("Include labeled delegate-creation references.")] bool includeDelegates = false) =>
        await FindRelationshipsAsync(
            "find_callers",
            selector,
            codebase,
            channel,
            buildId,
            limit,
            ct,
            scope,
            collection,
            RelationshipDirection.Callers,
            cursor,
            exact,
            includeGenerated,
            includeDelegates);

    [McpServerTool(Name = "find_callees", Title = "Find callees", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find outgoing call-like relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCalleesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Include compiler-generated members with raw sources.")] bool includeGenerated = false,
        [Description("Include labeled delegate-creation references.")] bool includeDelegates = false) =>
        await FindRelationshipsAsync("find_callees", selector, codebase, channel, buildId, limit, ct, scope, collection, RelationshipDirection.Callees, cursor, includeGenerated: includeGenerated, includeDelegates: includeDelegates);

    [McpServerTool(Name = "find_references", Title = "Find references", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find incoming and outgoing relationships for one resolved game or local reference symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindReferencesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Include compiler-generated members with raw sources.")] bool includeGenerated = false) =>
        await FindRelationshipsAsync(
            "find_references",
            selector,
            codebase,
            channel,
            buildId,
            limit,
            ct,
            scope,
            collection,
            RelationshipDirection.References,
            cursor,
            includeGenerated: includeGenerated);

    [McpServerTool(Name = "find_call_sites", Title = "Find call sites", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find recovered-IL static call-site references for a game member or canonical raw target text; results do not prove runtime behavior or call order.")]
    public async Task<ToolEnvelope<CallSiteQueryResult>> FindCallSitesAsync(
        [Description("Resolved game-member selector or canonical raw target text.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<CallSiteQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<CallSiteQueryResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryDecodeCursor<CallSiteQueryResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            return await WithApiSelectionAsync<CallSiteQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.CallSitesSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        ct,
                        offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_call_sites", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        scope.ToString(), collection);
                    if (!ToolArguments.VerifyCursorHash<CallSiteQueryResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiCallSites(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<CallSiteQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

                if (!ToolArguments.TryDecodeCursor<CallSiteQueryResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<CallSiteQueryResult> scopeError, offset))
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
                        ct,
                        offset)
                    : await _services.FederatedIndexQueryService.CallSitesAsync(
                        selector,
                        options,
                        ct,
                        pinned.ReferenceCollection?.ReferenceIndexId);
                var expectedHash = ToolArguments.CursorHashFor(
                    "find_call_sites", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    scope.ToString(), collection);
                if (!ToolArguments.VerifyCursorHash<CallSiteQueryResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedCallSites(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor },
                    options.ReferenceCollection, pinned.ReferenceCollection?.ReferenceIndexId);
            });
    }

    [McpServerTool(Name = "find_field_references", Title = "Find field references", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find recovered-IL static field readers and writers for one resolved game or local reference field; results do not prove lifecycle ordering or runtime behavior.")]
    public async Task<ToolEnvelope<FieldReferenceQueryResult>> FindFieldReferencesAsync(
        [Description("Exact or fuzzy field selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Return only field readers.")] bool readers = false,
        [Description("Return only field writers.")] bool writers = false,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null,
        [Description("Include compiler-generated members with raw sources.")] bool includeGenerated = false)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<FieldReferenceQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (readers && writers)
            {
                return EnvelopeMapper.Invalid<FieldReferenceQueryResult>("InvalidFieldFilter", "Choose readers or writers, not both.");
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<FieldReferenceQueryResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryDecodeCursor<FieldReferenceQueryResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            var filter = readers
                ? FieldReferenceFilter.Readers
                : writers
                    ? FieldReferenceFilter.Writers
                    : FieldReferenceFilter.All;
            return await WithApiSelectionAsync<FieldReferenceQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.FieldReferencesSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        filter,
                        ct,
                        includeGenerated,
                        offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_field_references", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        scope.ToString(), collection, readers.ToString(), writers.ToString(), includeGenerated.ToString());
                    if (!ToolArguments.VerifyCursorHash<FieldReferenceQueryResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiFieldReferences(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<FieldReferenceQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

                if (!ToolArguments.TryDecodeCursor<FieldReferenceQueryResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<FieldReferenceQueryResult> scopeError, offset))
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
                        ct,
                        includeGenerated,
                        offset)
                    : await _services.FederatedIndexQueryService.FieldReferencesAsync(
                        selector,
                        options,
                        filter,
                        ct,
                        pinned.ReferenceCollection?.ReferenceIndexId,
                        includeGenerated);
                var expectedHash = ToolArguments.CursorHashFor(
                    "find_field_references", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    scope.ToString(), collection, readers.ToString(), writers.ToString(), includeGenerated.ToString());
                if (!ToolArguments.VerifyCursorHash<FieldReferenceQueryResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedFieldReferences(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor },
                    options.ReferenceCollection, pinned.ReferenceCollection?.ReferenceIndexId);
            });
    }

    [McpServerTool(Name = "find_related_types", Title = "Find related types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find type-oriented relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelatedTypesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Optional type relationship kinds to include.")] string[]? relationKinds = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<RelationshipQuerySetResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<RelationshipQuerySetResult> limitError))
            {
                return limitError;
            }

            if (relationKinds is not null && relationKinds.Any(kind => !RelatedTypeRelationshipKinds.Contains(kind)))
            {
                return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidRelationshipKind", "Unsupported API type relationship kind.");
            }

            if (!ToolArguments.TryDecodeCursor<RelationshipQuerySetResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            var kindsHash = relationKinds is null
                ? string.Join(",", RelatedTypeRelationshipKinds.OrderBy(kind => kind, StringComparer.Ordinal))
                : string.Join(",", relationKinds.OrderBy(kind => kind, StringComparer.Ordinal));
            return await WithApiSelectionAsync<RelationshipQuerySetResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.RelationshipsSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        ApiRelationshipDirection.References,
                        relationKinds is null
                            ? null
                            : new HashSet<string>(relationKinds, StringComparer.OrdinalIgnoreCase),
                        ct,
                        offset: offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_related_types", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        scope.ToString(), collection, kindsHash);
                    if (!ToolArguments.VerifyCursorHash<RelationshipQuerySetResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiRelationships(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

                if (!ToolArguments.TryDecodeCursor<RelationshipQuerySetResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<RelationshipQuerySetResult> scopeError, offset))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<RelationshipQuerySetResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                // The kind filter applies after the fetch, so the fetch must
                // cover every edge: a capped window would silently drop rows
                // past the cap and under-report the total.
                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.RefsInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        int.MaxValue,
                        ct)
                    : await _services.FederatedIndexQueryService.RefsAsync(
                        selector,
                        options with { Limit = int.MaxValue, Offset = 0 },
                        ct);
                if (result.Resolution.Status == SymbolResolutionStatus.Resolved)
                {
                    var filtered = result.Relationships
                        .Where(edge => selectedKinds.Contains(edge.Kind))
                        .ToArray();
                    var (rows, hasMore) = IndexPaging.TakePage(filtered, offset, boundedLimit);
                    result = result with
                    {
                        Relationships = rows,
                        TotalCount = filtered.Length,
                        HasMore = hasMore
                    };
                }

                var expectedHash = ToolArguments.CursorHashFor(
                    "find_related_types", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    scope.ToString(), collection,
                    string.Join(",", selectedKinds.OrderBy(kind => kind, StringComparer.Ordinal)));
                if (!ToolArguments.VerifyCursorHash<RelationshipQuerySetResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedRelationships(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor }, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "find_overrides", Title = "Find overrides", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find the base and interface slots one method fills, up to the root.")]
    public async Task<ToolEnvelope<HierarchyQueryResult>> FindOverridesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<HierarchyQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            return await WithApiSelectionAsync<HierarchyQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.OverridesSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        ct,
                        offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_overrides", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        scope.ToString(), collection);
                    if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiHierarchy(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<HierarchyQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<HierarchyQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<HierarchyQueryResult> scopeError, offset))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<HierarchyQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.OverridesInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        ct,
                        offset)
                    : await _services.FederatedIndexQueryService.OverridesAsync(
                        selector,
                        options,
                        ct,
                        offset: offset);

                var expectedHash = ToolArguments.CursorHashFor(
                    "find_overrides", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    scope.ToString(), collection);
                if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedHierarchy(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor }, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "find_overriders", Title = "Find overriders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find the methods that override or implement one method, transitively.")]
    public async Task<ToolEnvelope<HierarchyQueryResult>> FindOverridersAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        [Description("Maximum hierarchy depth to traverse.")] int depth = 10,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<HierarchyQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryBoundDepth(depth, null, out var boundedDepth, out ToolEnvelope<HierarchyQueryResult> depthError))
            {
                return depthError;
            }

            if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            return await WithApiSelectionAsync<HierarchyQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.OverridersSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        boundedDepth,
                        ct,
                        offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_overriders", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        boundedDepth.ToString(), scope.ToString(), collection);
                    if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiHierarchy(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<HierarchyQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<HierarchyQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryBoundDepth(depth, authority, out var boundedDepth, out ToolEnvelope<HierarchyQueryResult> depthError))
                {
                    return depthError;
                }

                if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<HierarchyQueryResult> scopeError, offset))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<HierarchyQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.OverriddenByInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        boundedDepth,
                        ct,
                        offset)
                    : await _services.FederatedIndexQueryService.OverriddenByAsync(
                        selector,
                        options,
                        boundedDepth,
                        ct,
                        offset: offset);

                var expectedHash = ToolArguments.CursorHashFor(
                    "find_overriders", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    boundedDepth.ToString(), scope.ToString(), collection);
                if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedHierarchy(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor }, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "find_derived_types", Title = "Find derived types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Find the subclasses and implementers of one type, transitively.")]
    public async Task<ToolEnvelope<HierarchyQueryResult>> FindDerivedTypesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Codebase to query.")] McpCodebase codebase = McpCodebase.scheduleI,
        [Description("Channel; scheduleI has Installed only.")] CodeChannel channel = CodeChannel.Installed,
        [Description("Build ID; omit for current.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        [Description("Maximum hierarchy depth to traverse.")] int depth = 10,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null,
        [Description("Which indexes to query.")] IndexQueryScope scope = IndexQueryScope.Game,
        [Description("Collection ID or reference index ID for reference/all scope.")] string? collection = null)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<HierarchyQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryBoundDepth(depth, null, out var boundedDepth, out ToolEnvelope<HierarchyQueryResult> depthError))
            {
                return depthError;
            }

            if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            return await WithApiSelectionAsync<HierarchyQueryResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.DerivedSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        boundedDepth,
                        offset,
                        ct);
                    var expectedHash = ToolArguments.CursorHashFor(
                        "find_derived_types", catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        boundedDepth.ToString(), scope.ToString(), collection);
                    if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiHierarchy(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<HierarchyQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

        return await EnvelopeMapper.WithAuthorityAsync(
            _services.AuthorityResolver,
            buildId,
            ct,
            async authority =>
            {
                if (ToolArguments.TryValidateSelector(selector, authority, out ToolEnvelope<HierarchyQueryResult> selectorError))
                {
                    return selectorError;
                }

                if (!ToolArguments.TryBoundLimit(limit, authority, out var boundedLimit, out ToolEnvelope<HierarchyQueryResult> limitError))
                {
                    return limitError;
                }

                if (!ToolArguments.TryBoundDepth(depth, authority, out var boundedDepth, out ToolEnvelope<HierarchyQueryResult> depthError))
                {
                    return depthError;
                }

                if (!ToolArguments.TryDecodeCursor<HierarchyQueryResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<HierarchyQueryResult> scopeError, offset))
                {
                    return scopeError;
                }
                var pinned = await PinAuthorityAsync<HierarchyQueryResult>(authority, buildId, options, ct);
                if (pinned.Error is not null)
                    return pinned.Error;
                authority = pinned.Authority;
                options = options with { Limit = boundedLimit };

                var result = options.Scope == IndexQueryScope.Game
                    ? await _services.IndexQueryService.DerivedInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        boundedDepth,
                        offset,
                        ct)
                    : await _services.FederatedIndexQueryService.DerivedAsync(
                        selector,
                        options,
                        boundedDepth,
                        offset,
                        ct);

                var expectedHash = ToolArguments.CursorHashFor(
                    "find_derived_types", authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    boundedDepth.ToString(), scope.ToString(), collection);
                if (!ToolArguments.VerifyCursorHash<HierarchyQueryResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedHierarchy(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor }, options.ReferenceCollection);
            });
    }

    private async Task<ToolEnvelope<SymbolQueryResult>> GetSymbolAsync(
        string selector,
        McpCodebase codebase,
        CodeChannel channel,
        string? buildId,
        IReadOnlySet<SymbolKind> kinds,
        int limit,
        CancellationToken ct)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<SymbolQueryResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<SymbolQueryResult> limitError))
            {
                return limitError;
            }

            return await WithApiSelectionAsync<SymbolQueryResult>(
                codebase,
                channel,
                buildId,
                IndexQueryScope.Game,
                null,
                async (catalog, selection) =>
                {
                    var resolution = await _services.ApiIndexQueryService.ResolveSelectedAsync(
                        selection,
                        selector,
                        ct,
                        kinds);
                    if (resolution.Status == SymbolResolutionStatus.Ambiguous && resolution.Candidates.Count > boundedLimit)
                    {
                        resolution = resolution with { Candidates = resolution.Candidates.Take(boundedLimit).ToArray() };
                    }

                    return EnvelopeMapper.FromApiTypeMethod(catalog, selection, resolution, kinds);
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<SymbolQueryResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

                var resolution = await _services.IndexQueryService.ResolveInIndexAsync(
                    authority.IndexRun!,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    selector,
                    ct,
                    kinds);
                if (resolution.Status == SymbolResolutionStatus.Ambiguous && resolution.Candidates.Count > boundedLimit)
                {
                    resolution = resolution with { Candidates = resolution.Candidates.Take(boundedLimit).ToArray() };
                }

                return EnvelopeMapper.FromSymbolResolution(authority, resolution, kinds);
            });
    }

    private async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelationshipsAsync(
        string toolName,
        string selector,
        McpCodebase codebase,
        CodeChannel channel,
        string? buildId,
        int limit,
        CancellationToken ct,
        IndexQueryScope scope,
        string? collection,
        RelationshipDirection direction,
        string? cursor,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
    {
        if (codebase is not McpCodebase.scheduleI)
        {
            if (ToolArguments.TryValidateSelector<RelationshipQuerySetResult>(selector, null, out var selectorError))
            {
                return selectorError;
            }

            if (!ToolArguments.TryBoundLimit(limit, null, out var boundedLimit, out ToolEnvelope<RelationshipQuerySetResult> limitError))
            {
                return limitError;
            }

            if (!ToolArguments.TryDecodeCursor<RelationshipQuerySetResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
            {
                return cursorError;
            }

            var apiDirection = direction switch
            {
                RelationshipDirection.Callers => ApiRelationshipDirection.Callers,
                RelationshipDirection.Callees => ApiRelationshipDirection.Callees,
                _ => ApiRelationshipDirection.References
            };
            return await WithApiSelectionAsync<RelationshipQuerySetResult>(
                codebase,
                channel,
                buildId,
                scope,
                collection,
                async (catalog, selection) =>
                {
                    var result = await _services.ApiIndexQueryService.RelationshipsSelectedAsync(
                        selection,
                        selector,
                        boundedLimit,
                        apiDirection,
                        null,
                        ct,
                        exact,
                        includeGenerated,
                        includeDelegates,
                        offset);
                    var expectedHash = ToolArguments.CursorHashFor(
                        toolName, catalog.ResolvedBuildId, selection.IndexId,
                        selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                        scope.ToString(), collection, exact.ToString(), includeGenerated.ToString(), includeDelegates.ToString());
                    if (!ToolArguments.VerifyCursorHash<RelationshipQuerySetResult>(cursorHash, expectedHash, null, out var hashError))
                    {
                        return hashError;
                    }

                    var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                    return EnvelopeMapper.FromApiRelationships(
                        catalog, selection, nextCursor is null ? result : result with { NextCursor = nextCursor });
                },
                ct);
        }

        if (channel != CodeChannel.Installed)
        {
            return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidChannel", "Only the Installed channel exists for the scheduleI codebase.");
        }

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

                if (!ToolArguments.TryDecodeCursor<RelationshipQuerySetResult>(cursor, authority, out var cursorHash, out var offset, out var cursorError))
                {
                    return cursorError;
                }

                if (!ToolArguments.TryParseScope(scope, collection, authority, out var options, out ToolEnvelope<RelationshipQuerySetResult> scopeError, offset))
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
                        RelationshipDirection.Callers => await _services.IndexQueryService.CallersInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct, exact, includeGenerated, includeDelegates, offset),
                        RelationshipDirection.Callees => await _services.IndexQueryService.CalleesInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct, includeGenerated, includeDelegates, offset),
                        _ => await _services.IndexQueryService.RefsInIndexAsync(authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, boundedLimit, ct, includeGenerated, offset)
                    }
                    : direction switch
                    {
                        RelationshipDirection.Callers => await _services.FederatedIndexQueryService.CallersAsync(selector, options, ct, exact, includeGenerated: includeGenerated, includeDelegates: includeDelegates),
                        RelationshipDirection.Callees => await _services.FederatedIndexQueryService.CalleesAsync(selector, options, ct, includeGenerated: includeGenerated, includeDelegates: includeDelegates),
                        _ => await _services.FederatedIndexQueryService.RefsAsync(selector, options, ct, includeGenerated: includeGenerated)
                    };
                var expectedHash = ToolArguments.CursorHashFor(
                    toolName, authority.ResolvedBuildId, authority.IndexId,
                    selector, codebase.ToString(), channel.ToString(), buildId, boundedLimit.ToString(),
                    scope.ToString(), collection, exact.ToString(), includeGenerated.ToString(), includeDelegates.ToString());
                if (!ToolArguments.VerifyCursorHash<RelationshipQuerySetResult>(cursorHash, expectedHash, authority, out var hashError))
                {
                    return hashError;
                }

                var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
                return EnvelopeMapper.FromScopedRelationships(
                    authority, nextCursor is null ? result : result with { NextCursor = nextCursor }, options.ReferenceCollection);
            });
    }

    [McpServerTool(Name = "list_api_indexes", Title = "List API indexes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List completed S1API and S1MAPI indexes.")]
    public async Task<ToolEnvelope<ApiIndexCatalogResult>> ListApiIndexesAsync(
        [Description("Optional Schedule I build ID used only to select installed API indexes.")] string? buildId = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null)
    {
        if (!ToolArguments.TryBoundLimit<ApiIndexCatalogResult>(limit, null, out var boundedLimit, out var limitError))
        {
            return limitError;
        }

        if (!ToolArguments.TryDecodeCursor<ApiIndexCatalogResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
        {
            return cursorError;
        }

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var result = await _services.ApiIndexQueryService.ListAsync(buildId, ct, boundedLimit, offset);
            var expectedHash = ToolArguments.CursorHashFor(
                "list_api_indexes", result.ResolvedBuildId, result.RequestedBuildId, buildId, boundedLimit.ToString());
            if (!ToolArguments.VerifyCursorHash<ApiIndexCatalogResult>(cursorHash, expectedHash, null, out var hashError))
            {
                return hashError;
            }

            var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
            return EnvelopeMapper.FromApiCatalog(nextCursor is null ? result : result with { NextCursor = nextCursor });
        });
    }

    private static CodebaseKind MapCodebase(McpCodebase codebase) => codebase switch
    {
        McpCodebase.s1api => CodebaseKind.S1Api,
        McpCodebase.s1mapi => CodebaseKind.S1MApi,
        _ => CodebaseKind.ScheduleI
    };

    private async Task<ToolEnvelope<T>> WithApiSelectionAsync<T>(
        McpCodebase codebase,
        CodeChannel channel,
        string? buildId,
        IndexQueryScope scope,
        string? collection,
        Func<ApiIndexCatalogResult, ApiIndexSelection, Task<ToolEnvelope<T>>> query,
        CancellationToken ct) where T : class
    {
        if (!ToolArguments.TryParseScope<T>(scope, collection, null, out var options, out var scopeError))
        {
            return scopeError;
        }

        if (options.Scope != IndexQueryScope.Game)
        {
            return EnvelopeMapper.Invalid<T>("InvalidScope", "Reference scope requires the scheduleI codebase.");
        }

        var code = MapCodebase(codebase);
        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(buildId, ct);
            var selection = catalog.Selections.Single(selection => selection.Codebase == code && selection.Channel == channel);
            if (selection.Availability != ApiIndexAvailability.Current)
            {
                return EnvelopeMapper.FromApiSelectionFailure<T>(catalog, selection);
            }

            return await query(catalog, selection);
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

    internal static class ToolArguments
    {
        public const string InvalidCursorMessage =
            "The page cursor is invalid or was created for different arguments, a different build, or a different index.";

        public static bool TryDecodeCursor<T>(
            string? cursor,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            out string? cursorHash,
            out int offset,
            out ToolEnvelope<T> error) where T : class
        {
            cursorHash = null;
            offset = 0;
            if (cursor is null)
            {
                error = null!;
                return true;
            }

            if (!McpPageCursor.TryDecodeShape(cursor, out var hash, out var decoded))
            {
                error = Invalid<T>(authority, "InvalidCursor", InvalidCursorMessage);
                return false;
            }

            cursorHash = hash;
            offset = decoded;
            error = null!;
            return true;
        }

        public static bool VerifyCursorHash<T>(
            string? cursorHash,
            string expectedHash,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            out ToolEnvelope<T> error) where T : class
        {
            if (cursorHash is null || string.Equals(cursorHash, expectedHash, StringComparison.Ordinal))
            {
                error = null!;
                return true;
            }

            error = Invalid<T>(authority, "InvalidCursor", InvalidCursorMessage);
            return false;
        }

        public static string CursorHashFor(string tool, string? buildId, string? indexId, params string?[] args) =>
            McpPageCursor.HashFor(tool, args.Select(arg => arg ?? string.Empty), buildId, indexId);

        public static bool TryValidateSelector<T>(
            string? selector,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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

        public static bool TryBoundDepth<T>(
            int depth,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (depth <= 0)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidDepth", "The hierarchy depth must be positive.");
                return false;
            }

            bounded = depth;
            error = null!;
            return true;
        }

        public static bool TryBoundOffset<T>(
            int offset,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            out int bounded,
            out ToolEnvelope<T> error) where T : class
        {
            if (offset < 0)
            {
                bounded = default;
                error = Invalid<T>(authority, "InvalidOffset", "The hierarchy offset must not be negative.");
                return false;
            }

            bounded = offset;
            error = null!;
            return true;
        }

        public static bool TryParseScope<T>(
            IndexQueryScope scope,
            string? collection,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            out IndexQueryOptions options,
            out ToolEnvelope<T> error,
            int offset = 0) where T : class
        {
            var normalizedCollection = string.IsNullOrWhiteSpace(collection) ? null : collection.Trim();
            if (scope == IndexQueryScope.Game && normalizedCollection is not null)
            {
                options = null!;
                error = Invalid<T>(authority, "InvalidCollection", "A collection is valid only for reference or all scope.");
                return false;
            }

            if (scope is IndexQueryScope.Reference or IndexQueryScope.All && normalizedCollection is null)
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
                scope,
                normalizedCollection,
                offset);
            error = null!;
            return true;
        }

        public static bool TryParseRelationshipKinds<T>(
            string[]? relationKinds,
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
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
            S1Atlas.Application.Authority.InstalledBuildAuthority? authority,
            string code,
            string message) where T : class =>
            authority is null
                ? EnvelopeMapper.Invalid<T>(code, message)
                : ToolEnvelope<T>.Invalid(
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
