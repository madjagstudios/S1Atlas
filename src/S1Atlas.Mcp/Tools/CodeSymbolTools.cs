using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSymbolTools
{
    private static readonly HashSet<string> RelatedTypeRelationshipKinds =
    [
        "Inherits",
        "ImplementsInterface",
        "FieldType",
        "PropertyType",
        "EventType",
        "ParameterType",
        "ReturnType"
    ];

    private readonly McpReadOnlyServices _services;

    public CodeSymbolTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "search_symbols"), Description("Search the preferred, integrity-verified Schedule I code index for symbols.")]
    public async Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional symbol kind filter.")] string? kind,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var parsedKind = ToolArguments.ParseKind(kind);
            var boundedLimit = ToolArguments.BoundLimit(limit);
            return await EnvelopeMapper.WithAuthorityAsync(
                _services.AuthorityResolver,
                buildId,
                ct,
                async authority =>
                {
                    var result = await _services.IndexQueryService.SearchInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        query,
                        boundedLimit,
                        parsedKind,
                        ct);
                    return EnvelopeMapper.FromSearch(authority, result);
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<SymbolSearchResult>("InvalidLimit", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return EnvelopeMapper.Invalid<SymbolSearchResult>("InvalidKind", exception.Message);
        }
    }

    [McpServerTool(Name = "get_type"), Description("Resolve one type from the preferred, integrity-verified Schedule I code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetTypeAsync(
        [Description("Exact or fuzzy type selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, buildId, SymbolKind.Type, ct);

    [McpServerTool(Name = "get_method"), Description("Resolve one method from the preferred, integrity-verified Schedule I code index.")]
    public async Task<ToolEnvelope<SymbolQueryResult>> GetMethodAsync(
        [Description("Exact or fuzzy method selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        CancellationToken ct = default) =>
        await GetSymbolAsync(selector, buildId, SymbolKind.Method, ct);

    [McpServerTool(Name = "get_source"), Description("Return integrity-checked source for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Source context lines before and after the selected span.")] int context = 5,
        CancellationToken ct = default)
    {
        if (ToolArguments.TryValidateSelector(selector, out ToolEnvelope<SourceSnippetQueryResult> selectorError))
        {
            return selectorError;
        }

        try
        {
            var boundedContext = ToolArguments.BoundContext(context);
            return await EnvelopeMapper.WithAuthorityAsync(
                _services.AuthorityResolver,
                buildId,
                ct,
                async authority =>
                {
                    try
                    {
                        var result = await _services.IndexQueryService.SourceInIndexAsync(
                            authority.IndexRun!,
                            CodebaseKind.ScheduleI,
                            CodeChannel.Installed,
                            selector,
                            boundedContext,
                            ct);
                        return EnvelopeMapper.FromSource(authority, result);
                    }
                    catch (InvalidDataException exception)
                    {
                        return EnvelopeMapper.SourceIntegrityFailure<SourceSnippetQueryResult>(authority, exception.Message);
                    }
                    catch (FileNotFoundException exception)
                    {
                        return EnvelopeMapper.SourceUnavailable<SourceSnippetQueryResult>(authority, exception.Message);
                    }
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<SourceSnippetQueryResult>("InvalidContext", exception.Message);
        }
    }

    [McpServerTool(Name = "find_callers"), Description("Find incoming call-like relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCallersAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await FindRelationshipsAsync(
            selector,
            buildId,
            limit,
            ct,
            static (service, run, selectorValue, boundedLimit, token) =>
                service.CallersInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, selectorValue, boundedLimit, token));

    [McpServerTool(Name = "find_references"), Description("Find incoming and outgoing relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindReferencesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default) =>
        await FindRelationshipsAsync(
            selector,
            buildId,
            limit,
            ct,
            static (service, run, selectorValue, boundedLimit, token) =>
                service.RefsInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, selectorValue, boundedLimit, token));

    [McpServerTool(Name = "find_related_types"), Description("Find type-oriented relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelatedTypesAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default)
    {
        if (ToolArguments.TryValidateSelector(selector, out ToolEnvelope<RelationshipQuerySetResult> selectorError))
        {
            return selectorError;
        }

        try
        {
            var boundedLimit = ToolArguments.BoundLimit(limit);
            return await EnvelopeMapper.WithAuthorityAsync(
                _services.AuthorityResolver,
                buildId,
                ct,
                async authority =>
                {
                    var result = await _services.IndexQueryService.RefsInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        boundedLimit,
                        ct);
                    if (result.Resolution.Status == SymbolResolutionStatus.Resolved)
                    {
                        result = result with
                        {
                            Relationships = result.Relationships
                                .Where(edge => RelatedTypeRelationshipKinds.Contains(edge.Kind))
                                .ToArray()
                        };
                    }

                    return EnvelopeMapper.FromRelationships(authority, result);
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidLimit", exception.Message);
        }
    }

    private async Task<ToolEnvelope<SymbolQueryResult>> GetSymbolAsync(
        string selector,
        string? buildId,
        SymbolKind kind,
        CancellationToken ct)
    {
        if (ToolArguments.TryValidateSelector(selector, out ToolEnvelope<SymbolQueryResult> selectorError))
        {
            return selectorError;
        }

        try
        {
            return await EnvelopeMapper.WithAuthorityAsync(
                _services.AuthorityResolver,
                buildId,
                ct,
                async authority =>
                {
                    var results = await _services.IndexQueryService.FindInIndexAsync(
                        authority.IndexRun!,
                        CodebaseKind.ScheduleI,
                        CodeChannel.Installed,
                        selector,
                        kind,
                        50,
                        ct);
                    return EnvelopeMapper.FromFind(authority, results);
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<SymbolQueryResult>("InvalidLimit", exception.Message);
        }
    }

    private async Task<ToolEnvelope<RelationshipQuerySetResult>> FindRelationshipsAsync(
        string selector,
        string? buildId,
        int limit,
        CancellationToken ct,
        Func<S1Atlas.Indexing.Query.IndexQueryService, S1Atlas.Core.Storage.IndexRunRecord, string, int, CancellationToken, Task<RelationshipQuerySetResult>> query)
    {
        if (ToolArguments.TryValidateSelector(selector, out ToolEnvelope<RelationshipQuerySetResult> selectorError))
        {
            return selectorError;
        }

        try
        {
            var boundedLimit = ToolArguments.BoundLimit(limit);
            return await EnvelopeMapper.WithAuthorityAsync(
                _services.AuthorityResolver,
                buildId,
                ct,
                async authority =>
                {
                    var result = await query(_services.IndexQueryService, authority.IndexRun!, selector, boundedLimit, ct);
                    return EnvelopeMapper.FromRelationships(authority, result);
                });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return EnvelopeMapper.Invalid<RelationshipQuerySetResult>("InvalidLimit", exception.Message);
        }
    }

    private static class ToolArguments
    {
        public static bool TryValidateSelector<T>(
            string? selector,
            out ToolEnvelope<T> error) where T : class
        {
            if (!string.IsNullOrWhiteSpace(selector))
            {
                error = null!;
                return false;
            }

            error = EnvelopeMapper.Invalid<T>(
                "InvalidArguments",
                "The selector must not be blank or whitespace.");
            return true;
        }

        public static int BoundContext(int context)
        {
            if (context < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
            }

            return context;
        }

        public static int BoundLimit(int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");
            }

            return Math.Min(limit, 500);
        }

        public static SymbolKind? ParseKind(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return null;
            }

            if (Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException("Symbol kind must be Type, Constructor, Method, Field, Property, or Event.", nameof(kind));
        }
    }
}
