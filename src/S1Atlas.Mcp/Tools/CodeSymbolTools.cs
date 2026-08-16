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

    [McpServerTool(Name = "search_symbols"), Description("Search the preferred, integrity-verified Schedule I code index for symbols.")]
    public async Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Optional symbol kind filter.")] string? kind = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default)
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

    [McpServerTool(Name = "get_source"), Description("Return integrity-checked source for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<SourceSnippetQueryResult>> GetSourceAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Source context lines before and after the selected span.")] int context = 5,
        CancellationToken ct = default)
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

    [McpServerTool(Name = "find_callers"), Description("Find incoming call-like relationships for one resolved Schedule I symbol.")]
    public async Task<ToolEnvelope<RelationshipQuerySetResult>> FindCallersAsync(
        [Description("Exact or fuzzy symbol selector.")] string selector,
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
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
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
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
        [Description("Optional build ID; omitted resolves the current build.")] string? buildId = null,
        [Description("Optional type relationship kinds to include.")] string[]? relationKinds = null,
        [Description("Max results (1-500).")] int limit = 50,
        CancellationToken ct = default)
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

                var result = await _services.IndexQueryService.RefsInIndexAsync(
                    authority.IndexRun!,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    selector,
                    500,
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

                return EnvelopeMapper.FromRelationships(authority, result);
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
        Func<S1Atlas.Indexing.Query.IndexQueryService, S1Atlas.Core.Storage.IndexRunRecord, string, int, CancellationToken, Task<RelationshipQuerySetResult>> query)
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

                var result = await query(_services.IndexQueryService, authority.IndexRun!, selector, boundedLimit, ct);
                return EnvelopeMapper.FromRelationships(authority, result);
            });
    }

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
