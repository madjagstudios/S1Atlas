using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Application.Readiness;

namespace S1Atlas.Mcp;

// Central schema gate: when the atlas database is behind, ahead, or
// unrecognized, every tool call short-circuits with an unavailable envelope
// carrying the shared message and fix hint, before binding or any query runs.
internal static class McpSchemaGateFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        AtlasSchemaGate gate,
        JsonSerializerOptions json,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(next);

        return async (request, cancellationToken) =>
        {
            var status = await gate.GetStatusAsync(cancellationToken);
            var block = SchemaStatusWording.BlockFor(status);
            if (block is null)
            {
                return await next(request, cancellationToken);
            }

            var envelope = ToolEnvelope<object>.Unavailable(
                new ToolError(block.Code, block.Message, block.Hint));
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, json) }]
            };
        };
    }
}
