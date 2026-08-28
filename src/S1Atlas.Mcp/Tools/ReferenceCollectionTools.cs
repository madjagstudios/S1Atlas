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

    [McpServerTool(Name = "list_reference_collections"), Description("List completed local reference-mod collections and their recorded Schedule I base indexes.")]
    public async Task<ToolEnvelope<ReferenceCollectionListResult>> ListReferenceCollectionsAsync(
        CancellationToken ct = default)
    {
        return await EnvelopeMapper.WithAtlasAvailabilityAsync(async () =>
        {
            var result = await _services.ReferenceModQueryService.ListCollectionsAsync(ct);
            return ToolEnvelope<ReferenceCollectionListResult>.Resolved(
                null,
                result,
                new ProvenanceEntry(
                    ProvenanceClassification.Fact,
                    "reference-collection-indexes",
                    null,
                    null,
                    null));
        });
    }
}
