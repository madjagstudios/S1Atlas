using System.ComponentModel;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class ReferenceCollectionTools
{
    private readonly McpReadOnlyServices _services;

    public ReferenceCollectionTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "list_reference_collections", Title = "List reference collections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List completed reference-mod collections with their recorded base indexes.")]
    public async Task<ToolEnvelope<ReferenceCollectionListResult>> ListReferenceCollectionsAsync(
        CancellationToken ct = default,
        [Description("Max results (1-500).")] int limit = 50,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null)
    {
        if (!CodeSymbolTools.ToolArguments.TryBoundLimit<ReferenceCollectionListResult>(limit, null, out var boundedLimit, out var limitError))
        {
            return limitError;
        }

        if (!CodeSymbolTools.ToolArguments.TryDecodeCursor<ReferenceCollectionListResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
        {
            return cursorError;
        }

        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var result = await _services.ReferenceModQueryService.ListCollectionsAsync(ct, boundedLimit, offset);
            var expectedHash = CodeSymbolTools.ToolArguments.CursorHashFor(
                "list_reference_collections", null, result.TotalCount.ToString(), boundedLimit.ToString());
            if (!CodeSymbolTools.ToolArguments.VerifyCursorHash<ReferenceCollectionListResult>(cursorHash, expectedHash, null, out var hashError))
            {
                return hashError;
            }

            var nextCursor = McpPageCursor.MintNextCursor(result.HasMore, expectedHash, offset, boundedLimit);
            return ToolEnvelope<ReferenceCollectionListResult>.Resolved(
                null,
                nextCursor is null ? result : result with { NextCursor = nextCursor },
                new ProvenanceEntry(
                    ProvenanceClassification.Fact,
                    "reference-collection-indexes",
                    null,
                    null,
                    null));
        });
    }
}
