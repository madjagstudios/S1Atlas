using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace S1Atlas.Mcp;

/// <summary>
/// Flags domain-failure envelopes as MCP errors. Only upgrades results that are positively
/// identified as not_found, invalid, or unavailable envelopes; resolved and ambiguous
/// answers, binding failures already flagged upstream, and unrecognized payloads pass
/// through unchanged with their content intact.
/// </summary>
internal static class McpEnvelopeErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            if (result.IsError == true) return result;
            if (result.Content?.Count == 1 &&
                result.Content[0] is TextContentBlock text &&
                IsFailureEnvelope(text.Text))
            {
                result.IsError = true;
            }

            return result;
        };

    private static bool IsFailureEnvelope(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("status", out var status)) return false;
            return status.GetString() is
                "not_found" or "NotFound" or
                "invalid" or "Invalid" or
                "unavailable" or "Unavailable";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
