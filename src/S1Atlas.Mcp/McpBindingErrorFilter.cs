using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace S1Atlas.Mcp;

/// <summary>
/// Turns opaque SDK argument-binding failures into errors that name the offending parameter.
/// Only intervenes when the failure is positively identified as a binding problem; every other
/// exception propagates unchanged.
/// </summary>
internal static class McpBindingErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                if (TryMissingParameter(exception, out var name))
                {
                    return BindingError(request.Params, $"missing required argument '{name}'.");
                }

                throw;
            }
            catch (JsonException)
            {
                if (TryMismatchedArgument(request, out var detail))
                {
                    return BindingError(request.Params, detail);
                }

                throw;
            }
        };

    private static CallToolResult BindingError(CallToolRequestParams? parameters, string detail) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = $"Tool '{parameters?.Name}': {detail}" }] };

    private static bool TryMissingParameter(ArgumentException exception, out string name)
    {
        var match = Regex.Match(exception.Message, "required parameter '([^']+)'");
        name = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    private static bool TryMismatchedArgument(
        RequestContext<CallToolRequestParams> context,
        out string detail)
    {
        detail = string.Empty;
        if (context.Params?.Arguments is null) return false;
        if (context.MatchedPrimitive is not McpServerTool tool) return false;
        if (tool.ProtocolTool.InputSchema.ValueKind != JsonValueKind.Object) return false;
        if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)) return false;
        if (properties.ValueKind != JsonValueKind.Object) return false;

        foreach (var property in properties.EnumerateObject())
        {
            if (!context.Params.Arguments.TryGetValue(property.Name, out var value)) continue;
            if (!MatchesDeclaredType(value, property.Value, out var expected))
            {
                detail = $"argument '{property.Name}' must be {expected} but received {DescribeJson(value)}.";
                return true;
            }
        }

        return false;
    }

    private static bool MatchesDeclaredType(JsonElement value, JsonElement propertySchema, out string expected)
    {
        expected = string.Empty;
        if (!TryDeclaredTypes(propertySchema, out var declared)) return true;
        expected = string.Join(" or ", declared.Select(DescribeDeclaredType));

        foreach (var type in declared)
        {
            if (MatchesType(value, type, propertySchema))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesType(JsonElement value, string type, JsonElement propertySchema) =>
        type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && MatchesItems(value, propertySchema),
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
            _ => true
        };

    private static bool MatchesItems(JsonElement value, JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("items", out var items)) return true;
        if (!TryDeclaredTypes(items, out var declared)) return true;
        return value.EnumerateArray().All(element => declared.Any(type => MatchesType(element, type, items)));
    }

    private static bool TryDeclaredTypes(JsonElement schema, out IReadOnlyList<string> declared)
    {
        declared = [];
        if (!schema.TryGetProperty("type", out var type)) return false;
        if (type.ValueKind == JsonValueKind.String)
        {
            declared = [type.GetString()!];
            return true;
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            var types = type.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!)
                .ToArray();
            if (types.Length == 0) return false;
            declared = types;
            return true;
        }

        return false;
    }

    private static string DescribeDeclaredType(string type) =>
        type switch
        {
            "string" => "a string",
            "integer" => "an integer",
            "number" => "a number",
            "boolean" => "a boolean",
            "array" => "an array",
            "object" => "an object",
            "null" => "null",
            _ => $"a {type}"
        };

    private static string DescribeJson(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "a JSON string",
            JsonValueKind.Number => "a JSON number",
            JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
            JsonValueKind.Array => "a JSON array",
            JsonValueKind.Object => "a JSON object",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => "an unsupported JSON value"
        };
}
