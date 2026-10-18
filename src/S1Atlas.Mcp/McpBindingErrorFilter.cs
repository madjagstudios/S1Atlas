using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;

namespace S1Atlas.Mcp;

/// <summary>
/// Turns opaque SDK argument-binding failures into invalid_arguments envelopes that name the
/// offending parameter. Only intervenes when the failure is positively identified as a binding
/// problem; every other exception propagates unchanged.
/// </summary>
internal static class McpBindingErrorFilter
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Type>> EnumParameters =
        McpToolCatalog.DiscoverEnumParameters();

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        JsonSerializerOptions json,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(next);

        return async (request, cancellationToken) =>
        {
            if (TryUndefinedEnumNumber(request.Params, out var enumDetail))
            {
                return BindingError(json, request.Params, enumDetail);
            }

            try
            {
                return await next(request, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                if (TryMissingParameter(exception, out var name))
                {
                    return BindingError(json, request.Params, $"missing required argument '{name}'.");
                }

                throw;
            }
            catch (JsonException)
            {
                if (TryMismatchedArgument(request, out var detail))
                {
                    return BindingError(json, request.Params, detail);
                }

                throw;
            }
        };
    }

    // Same envelope shape as every other argument failure, so agents that
    // branch on error.code see invalid_arguments.
    private static CallToolResult BindingError(JsonSerializerOptions json, CallToolRequestParams? parameters, string detail)
    {
        var envelope = ToolEnvelope<object>.Invalid(
            new ToolError("InvalidArguments", $"Tool '{parameters?.Name}': {detail}"));
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, json) }]
        };
    }

    // The SDK binds a JSON number into an enum parameter without checking it,
    // so an undefined value (codebase: 99) would reach tool code. Every tool
    // enum is int-backed; numbers outside int or not naming a member fail here.
    private static bool TryUndefinedEnumNumber(CallToolRequestParams? parameters, out string detail)
    {
        detail = string.Empty;
        if (parameters?.Arguments is null) return false;
        if (!EnumParameters.TryGetValue(parameters.Name, out var enums)) return false;

        foreach (var (name, enumType) in enums)
        {
            if (!parameters.Arguments.TryGetValue(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number) continue;
            if (value.TryGetInt32(out var number) && Enum.IsDefined(enumType, Enum.ToObject(enumType, number))) continue;

            detail = $"argument '{name}' must be one of {string.Join(", ", Enum.GetNames(enumType))} but received {value.GetRawText()}.";
            return true;
        }

        return false;
    }

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
            if (TryEnumMismatch(value, property.Value, out var allowed))
            {
                detail = $"argument '{property.Name}' must be one of {allowed} but received {DescribeEnumValue(value)}.";
                return true;
            }

            if (IsBoundEnumNumber(value, property.Value)) continue;
            if (!MatchesDeclaredType(value, property.Value, out var expected))
            {
                detail = $"argument '{property.Name}' must be {expected} but received {DescribeJson(value)}.";
                return true;
            }
        }

        return false;
    }

    // A schema "enum" array marks a fixed vocabulary. Binding accepts member
    // names case-insensitively, and undefined numbers are rejected before
    // binding (TryUndefinedEnumNumber), so only a string outside the
    // vocabulary (or a null against a non-nullable enum) is positively a
    // binding failure here.
    private static bool TryEnumMismatch(JsonElement value, JsonElement propertySchema, out string allowed)
    {
        allowed = string.Empty;
        if (!propertySchema.TryGetProperty("enum", out var members) || members.ValueKind != JsonValueKind.Array)
            return false;
        var names = members.EnumerateArray()
            .Where(member => member.ValueKind == JsonValueKind.String)
            .Select(member => member.GetString()!)
            .ToArray();
        if (names.Length == 0) return false;

        if (value.ValueKind == JsonValueKind.String)
        {
            if (names.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase)) return false;
        }
        else if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (members.EnumerateArray().Any(member => member.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
                return false;
        }
        else
        {
            return false;
        }

        allowed = string.Join(", ", names);
        return true;
    }

    private static bool IsBoundEnumNumber(JsonElement value, JsonElement propertySchema) =>
        value.ValueKind == JsonValueKind.Number &&
        propertySchema.TryGetProperty("enum", out var members) &&
        members.ValueKind == JsonValueKind.Array;

    private static string DescribeEnumValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? $"\"{value.GetString()}\"" : DescribeJson(value);

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
