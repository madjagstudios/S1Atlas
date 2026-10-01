using System.Text.Json;
using S1Atlas.Application.Envelope;

namespace S1Atlas.Web;

// The JSON options for every /api/* response: the same options the MCP tools
// use, so API output has the same shape as MCP output.
internal static class ServeApiJson
{
    internal static readonly JsonSerializerOptions Options = ToolJsonOptions.Create();
}
