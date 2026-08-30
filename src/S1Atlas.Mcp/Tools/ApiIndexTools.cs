using System.ComponentModel;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class ApiIndexTools
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

    public ApiIndexTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "list_api_indexes"), Description("List completed S1API and S1MAPI indexes available to the read-only Atlas host.")]
    public async Task<ToolEnvelope<ApiIndexCatalogResult>> ListApiIndexesAsync(
        [Description("Optional Schedule I build ID used only to select installed API indexes.")] string? buildId = null,
        CancellationToken ct = default) =>
        await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var result = await _services.ApiIndexQueryService.ListAsync(buildId, ct);
            return EnvelopeMapper.FromApiCatalog(result);
        });

    [McpServerTool(Name = "search_api_symbols"), Description("Search a completed S1API or S1MAPI index.")]
    public async Task<ToolEnvelope<SymbolSearchResult>> SearchApiSymbolsAsync(
        [Description("API codebase: s1api or s1mapi.")] string codebase,
        [Description("API channel: installed, release, or preview.")] string channel,
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default)
    {
        if (!TryParseScope<SymbolSearchResult>(codebase, channel, out var parsedCodebase, out var parsedChannel, out var scopeError))
            return scopeError;
        if (!TryValidateSelector(query, out ToolEnvelope<SymbolSearchResult> selectorError))
            return selectorError;
        if (!TryBoundLimit(limit, out var boundedLimit, out ToolEnvelope<SymbolSearchResult> limitError))
            return limitError;

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(buildId: null, ct);
            var selection = Select(catalog, parsedCodebase, parsedChannel);
            if (selection.Availability != ApiIndexAvailability.Current)
                return EnvelopeMapper.FromApiSelectionFailure<SymbolSearchResult>(catalog, selection);

            var result = await _services.ApiIndexQueryService.SearchSelectedAsync(
                selection,
                query,
                boundedLimit,
                ct);
            return EnvelopeMapper.FromApiSearch(catalog, selection, result);
        });
    }

    [McpServerTool(Name = "get_api_source"), Description("Return integrity-checked source for a resolved S1API or S1MAPI symbol.")]
    public async Task<ToolEnvelope<SourceSnippetQueryResult>> GetApiSourceAsync(
        [Description("API codebase: s1api or s1mapi.")] string codebase,
        [Description("API channel: installed, release, or preview.")] string channel,
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Source context lines before and after the selected span.")] int context = 5,
        [Description("Max caller/callee neighborhood rows per direction (0-50). Zero disables neighborhood lookup.")] int relatedLimit = 10,
        CancellationToken ct = default)
    {
        if (!TryParseScope<SourceSnippetQueryResult>(codebase, channel, out var parsedCodebase, out var parsedChannel, out var scopeError))
            return scopeError;
        if (!TryValidateSelector(selector, out ToolEnvelope<SourceSnippetQueryResult> selectorError))
            return selectorError;
        if (context < 0)
        {
            return EnvelopeMapper.Invalid<SourceSnippetQueryResult>(
                "InvalidContext",
                "Source context cannot be negative.");
        }
        if (relatedLimit is < 0 or > 50)
        {
            return EnvelopeMapper.Invalid<SourceSnippetQueryResult>(
                "InvalidRelatedLimit",
                "The related result limit must be between 0 and 50.");
        }

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(buildId: null, ct);
            var selection = Select(catalog, parsedCodebase, parsedChannel);
            if (selection.Availability != ApiIndexAvailability.Current)
                return EnvelopeMapper.FromApiSelectionFailure<SourceSnippetQueryResult>(catalog, selection);

            try
            {
                var result = await _services.ApiIndexQueryService.SourceSelectedAsync(
                    selection,
                    selector,
                    context,
                    relatedLimit,
                    ct);
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
        });
    }

    [McpServerTool(Name = "find_api_callers"), Description("Find incoming call-like relationships in a completed S1API or S1MAPI index.")]
    public Task<ToolEnvelope<RelationshipQuerySetResult>> FindApiCallersAsync(
        string codebase,
        string channel,
        string selector,
        int limit = 50,
        CancellationToken ct = default) =>
        QueryApiRelationshipsAsync(codebase, channel, selector, limit, ApiRelationshipDirection.Callers, null, ct);

    [McpServerTool(Name = "find_api_callees"), Description("Find outgoing call-like relationships in a completed S1API or S1MAPI index.")]
    public Task<ToolEnvelope<RelationshipQuerySetResult>> FindApiCalleesAsync(
        string codebase,
        string channel,
        string selector,
        int limit = 50,
        CancellationToken ct = default) =>
        QueryApiRelationshipsAsync(codebase, channel, selector, limit, ApiRelationshipDirection.Callees, null, ct);

    [McpServerTool(Name = "find_api_references"), Description("Find incoming and outgoing relationships in a completed S1API or S1MAPI index.")]
    public Task<ToolEnvelope<RelationshipQuerySetResult>> FindApiReferencesAsync(
        string codebase,
        string channel,
        string selector,
        int limit = 50,
        CancellationToken ct = default) =>
        QueryApiRelationshipsAsync(codebase, channel, selector, limit, ApiRelationshipDirection.References, null, ct);

    [McpServerTool(Name = "find_api_related_types"), Description("Find inheritance, interface, and other type relationships in a completed S1API or S1MAPI index.")]
    public Task<ToolEnvelope<RelationshipQuerySetResult>> FindApiRelatedTypesAsync(
        string codebase,
        string channel,
        string selector,
        string[]? relationKinds = null,
        int limit = 50,
        CancellationToken ct = default) =>
        QueryApiRelationshipsAsync(codebase, channel, selector, limit, ApiRelationshipDirection.References, relationKinds, ct);

    [McpServerTool(Name = "find_api_call_sites"), Description("Find recovered-IL static call sites in a completed S1API or S1MAPI index.")]
    public async Task<ToolEnvelope<CallSiteQueryResult>> FindApiCallSitesAsync(
        string codebase,
        string channel,
        string selector,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (!TryParseScope<CallSiteQueryResult>(codebase, channel, out var parsedCodebase, out var parsedChannel, out var scopeError))
            return scopeError;
        if (!TryValidateSelector(selector, out ToolEnvelope<CallSiteQueryResult> selectorError))
            return selectorError;
        if (!TryBoundLimit(limit, out var boundedLimit, out ToolEnvelope<CallSiteQueryResult> limitError))
            return limitError;

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(null, ct);
            var selection = Select(catalog, parsedCodebase, parsedChannel);
            if (selection.Availability != ApiIndexAvailability.Current)
                return EnvelopeMapper.FromApiSelectionFailure<CallSiteQueryResult>(catalog, selection);

            var result = await _services.ApiIndexQueryService.CallSitesSelectedAsync(
                selection, selector, boundedLimit, ct);
            return EnvelopeMapper.FromApiCallSites(catalog, selection, result);
        });
    }

    [McpServerTool(Name = "find_api_field_references"), Description("Find field readers and writers in a completed S1API or S1MAPI index.")]
    public async Task<ToolEnvelope<FieldReferenceQueryResult>> FindApiFieldReferencesAsync(
        string codebase,
        string channel,
        string selector,
        bool readers = false,
        bool writers = false,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (!TryParseScope<FieldReferenceQueryResult>(codebase, channel, out var parsedCodebase, out var parsedChannel, out var scopeError))
            return scopeError;
        if (!TryValidateSelector(selector, out ToolEnvelope<FieldReferenceQueryResult> selectorError))
            return selectorError;
        if (readers && writers)
            return EnvelopeMapper.Invalid<FieldReferenceQueryResult>("InvalidFieldFilter", "Choose readers or writers, not both.");
        if (!TryBoundLimit(limit, out var boundedLimit, out ToolEnvelope<FieldReferenceQueryResult> limitError))
            return limitError;

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(null, ct);
            var selection = Select(catalog, parsedCodebase, parsedChannel);
            if (selection.Availability != ApiIndexAvailability.Current)
                return EnvelopeMapper.FromApiSelectionFailure<FieldReferenceQueryResult>(catalog, selection);

            var filter = readers
                ? FieldReferenceFilter.Readers
                : writers
                    ? FieldReferenceFilter.Writers
                    : FieldReferenceFilter.All;
            var result = await _services.ApiIndexQueryService.FieldReferencesSelectedAsync(
                selection, selector, boundedLimit, filter, ct);
            return EnvelopeMapper.FromApiFieldReferences(catalog, selection, result);
        });
    }

    private async Task<ToolEnvelope<RelationshipQuerySetResult>> QueryApiRelationshipsAsync(
        string codebase,
        string channel,
        string selector,
        int limit,
        ApiRelationshipDirection direction,
        IReadOnlyList<string>? relationKinds,
        CancellationToken ct)
    {
        if (!TryParseScope<RelationshipQuerySetResult>(codebase, channel, out var parsedCodebase, out var parsedChannel, out var scopeError))
            return scopeError;
        if (!TryValidateSelector(selector, out ToolEnvelope<RelationshipQuerySetResult> selectorError))
            return selectorError;
        if (!TryBoundLimit(limit, out var boundedLimit, out ToolEnvelope<RelationshipQuerySetResult> limitError))
            return limitError;
        if (relationKinds is not null && relationKinds.Any(kind => !RelatedTypeRelationshipKinds.Contains(kind)))
            return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidRelationshipKind", "Unsupported API type relationship kind.");

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var catalog = await _services.ApiIndexQueryService.ListAsync(null, ct);
            var selection = Select(catalog, parsedCodebase, parsedChannel);
            if (selection.Availability != ApiIndexAvailability.Current)
                return EnvelopeMapper.FromApiSelectionFailure<RelationshipQuerySetResult>(catalog, selection);

            var result = await _services.ApiIndexQueryService.RelationshipsSelectedAsync(
                selection,
                selector,
                relationKinds is null ? boundedLimit : 500,
                direction,
                relationKinds is null
                    ? null
                    : new HashSet<string>(relationKinds, StringComparer.OrdinalIgnoreCase),
                ct);
            return EnvelopeMapper.FromApiRelationships(catalog, selection, result);
        });
    }

    private static bool TryParseScope<T>(
        string? codebase,
        string? channel,
        out CodebaseKind parsedCodebase,
        out CodeChannel parsedChannel,
        out ToolEnvelope<T> error) where T : class
    {
        var parsedCodebaseValue = codebase?.Trim().ToLowerInvariant() switch
        {
            "s1api" => (CodebaseKind?)CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => null
        };
        if (parsedCodebaseValue is null)
        {
            parsedCodebase = default;
            parsedChannel = default;
            error = EnvelopeMapper.Invalid<T>("InvalidCodebase", "API codebase must be s1api or s1mapi.");
            return false;
        }
        parsedCodebase = parsedCodebaseValue.Value;

        var parsedChannelValue = channel?.Trim().ToLowerInvariant() switch
        {
            "installed" => (CodeChannel?)CodeChannel.Installed,
            "release" => CodeChannel.Release,
            "preview" => CodeChannel.Preview,
            _ => null
        };
        if (parsedChannelValue is null)
        {
            parsedChannel = default;
            error = EnvelopeMapper.Invalid<T>("InvalidChannel", "API channel must be installed, release, or preview.");
            return false;
        }
        parsedChannel = parsedChannelValue.Value;

        error = null!;
        return true;
    }

    private static ApiIndexSelection Select(
        ApiIndexCatalogResult catalog,
        CodebaseKind codebase,
        CodeChannel channel) =>
        catalog.Selections.Single(selection =>
            selection.Codebase == codebase && selection.Channel == channel);

    private static bool TryValidateSelector<T>(string? selector, out ToolEnvelope<T> error) where T : class
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            error = null!;
            return true;
        }

        error = EnvelopeMapper.Invalid<T>("InvalidArguments", "The selector must not be blank or whitespace.");
        return false;
    }

    private static bool TryBoundLimit<T>(int limit, out int bounded, out ToolEnvelope<T> error) where T : class
    {
        if (limit <= 0)
        {
            bounded = default;
            error = EnvelopeMapper.Invalid<T>("InvalidLimit", "The query result limit must be positive.");
            return false;
        }

        bounded = Math.Min(limit, 500);
        error = null!;
        return true;
    }
}
