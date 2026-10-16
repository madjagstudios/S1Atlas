using System.Text;
using System.Text.Json;

namespace S1Atlas.Core.Metadata;

/// <summary>
/// Canonical JSON storage format for decoded custom attributes:
/// <c>{"type": "...", "fixed": [{"kind": "...", "type": "...", "value": ...}],
/// "named": [{"name": "...", "kind": "...", "type": "...", "value": ...}]}</c>.
/// Keys are written in this fixed order; value kinds serialize by name. Arrays nest
/// their elements as value objects under <c>value</c>; <c>Type</c> values carry the
/// referenced type name in <c>type</c> with a null <c>value</c>.
/// Round-trip conventions: primitives restore to the exact CLR type named by
/// <c>type</c>; enums have no underlying-type slot, so integral values restore as
/// <see cref="int"/> when in range, else <see cref="long"/>, else <see cref="ulong"/>
/// (narrower original backings widen). <c>System.Char</c> values serialize as their
/// UTF-16 code-unit number so lone surrogates survive (reads also accept a legacy
/// single-character string). Non-finite <c>System.Single</c>/<c>System.Double</c>
/// values serialize as the strings <c>"NaN"</c>, <c>"Infinity"</c>, <c>"-Infinity"</c>
/// (reads also accept <c>"+Infinity"</c>); finite values stay JSON numbers. Reads
/// tolerate a missing <c>type</c>, <c>fixed</c>, or <c>named</c>; anything else
/// malformed throws <see cref="JsonException"/>.
/// </summary>
public static class CustomAttributeJson
{
    public static string ToJson(DecodedCustomAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", attribute.AttributeTypeName);
            writer.WriteStartArray("fixed");
            foreach (var argument in attribute.FixedArguments)
                WriteValueObject(writer, argument);
            writer.WriteEndArray();
            writer.WriteStartArray("named");
            foreach (var argument in attribute.NamedArguments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", argument.Name);
                WriteValueBody(writer, argument.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static DecodedCustomAttribute FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Attribute JSON must be an object.");

        return new DecodedCustomAttribute(
            root.TryGetProperty("type", out var type) ? type.GetString() : null,
            ReadValueList(root, "fixed"),
            ReadNamedList(root));
    }

    private static void WriteValueObject(Utf8JsonWriter writer, CustomAttributeValue value)
    {
        writer.WriteStartObject();
        WriteValueBody(writer, value);
        writer.WriteEndObject();
    }

    private static void WriteValueBody(Utf8JsonWriter writer, CustomAttributeValue value)
    {
        writer.WriteString("kind", value.Kind.ToString());
        writer.WriteString("type", value.TypeName);
        writer.WritePropertyName("value");
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, CustomAttributeValue value)
    {
        switch (value.Kind)
        {
            case CustomAttributeValueKind.Null:
            case CustomAttributeValueKind.Type:
                writer.WriteNullValue();
                break;
            case CustomAttributeValueKind.String:
                writer.WriteStringValue((string?)value.Value);
                break;
            case CustomAttributeValueKind.Primitive:
            case CustomAttributeValueKind.Enum:
                if (value.Value is null)
                    writer.WriteNullValue();
                else if (value.Value is char character)
                    writer.WriteNumberValue((int)character);
                else if (value.Value is float single && !float.IsFinite(single))
                    writer.WriteStringValue(NonFiniteName(single));
                else if (value.Value is double floating && !double.IsFinite(floating))
                    writer.WriteStringValue(NonFiniteName(floating));
                else
                    JsonSerializer.Serialize(writer, value.Value, value.Value.GetType());
                break;
            case CustomAttributeValueKind.Array:
                writer.WriteStartArray();
                foreach (var element in value.Elements ?? [])
                    WriteValueObject(writer, element);
                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown attribute value kind.");
        }
    }

    private static IReadOnlyList<CustomAttributeValue> ReadValueList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var list))
            return [];
        if (list.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Attribute JSON '{property}' must be an array.");
        return list.EnumerateArray().Select(ReadValueObject).ToArray();
    }

    private static IReadOnlyList<CustomAttributeNamedArgument> ReadNamedList(JsonElement root)
    {
        if (!root.TryGetProperty("named", out var list))
            return [];
        if (list.ValueKind != JsonValueKind.Array)
            throw new JsonException("Attribute JSON 'named' must be an array.");
        return list.EnumerateArray().Select(element =>
        {
            if (!element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new JsonException("Attribute JSON named arguments require a string 'name'.");
            return new CustomAttributeNamedArgument(name.GetString() ?? string.Empty, ReadValueObject(element));
        }).ToArray();
    }

    private static CustomAttributeValue ReadValueObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Attribute JSON values must be objects.");
        if (!element.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<CustomAttributeValueKind>(kindElement.GetString(), out var kind))
            throw new JsonException("Attribute JSON values require a known 'kind'.");
        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (!element.TryGetProperty("value", out var value))
            throw new JsonException("Attribute JSON values require a 'value'.");

        return kind switch
        {
            CustomAttributeValueKind.Null => new CustomAttributeValue(kind, null, null, null),
            CustomAttributeValueKind.Type => new CustomAttributeValue(kind, null, type, null),
            CustomAttributeValueKind.String => new CustomAttributeValue(kind, value.ValueKind == JsonValueKind.Null ? null : RequireString(value), type, null),
            CustomAttributeValueKind.Primitive => new CustomAttributeValue(kind, ReadPrimitive(type, value), type, null),
            CustomAttributeValueKind.Enum => new CustomAttributeValue(kind, ReadEnumNumber(value), type, null),
            CustomAttributeValueKind.Array => new CustomAttributeValue(kind, null, type, ReadArrayElements(value)),
            _ => throw new JsonException($"Unknown attribute value kind '{kind}'."),
        };
    }

    private static IReadOnlyList<CustomAttributeValue> ReadArrayElements(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException("Attribute JSON array values require an array 'value'.");
        return value.EnumerateArray().Select(ReadValueObject).ToArray();
    }

    private static string RequireString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new JsonException("Attribute JSON string values require a string 'value'.");

    private static object? ReadPrimitive(string? type, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return type switch
        {
            "System.Boolean" => RequireValueKind(value, JsonValueKind.True, JsonValueKind.False).GetBoolean(),
            "System.Char" => ReadChar(value),
            "System.SByte" => RequireNumber(value).GetSByte(),
            "System.Byte" => RequireNumber(value).GetByte(),
            "System.Int16" => RequireNumber(value).GetInt16(),
            "System.UInt16" => RequireNumber(value).GetUInt16(),
            "System.Int32" => RequireNumber(value).GetInt32(),
            "System.UInt32" => RequireNumber(value).GetUInt32(),
            "System.Int64" => RequireNumber(value).GetInt64(),
            "System.UInt64" => RequireNumber(value).GetUInt64(),
            "System.Single" => ReadSingle(value),
            "System.Double" => ReadDouble(value),
            "System.String" => RequireString(value),
            _ => InferUntypedPrimitive(value),
        };
    }

    private static object? ReadEnumNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        var number = RequireNumber(value);
        if (number.TryGetInt32(out var intValue))
            return intValue;
        if (number.TryGetInt64(out var longValue))
            return longValue;
        if (number.TryGetUInt64(out var ulongValue))
            return ulongValue;
        throw new JsonException("Attribute JSON enum values require an integral 'value'.");
    }

    private static object ReadSingle(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return ParseSingle(value.GetString() ?? string.Empty);
        return RequireNumber(value).GetSingle();
    }

    private static object ReadDouble(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return ParseDouble(value.GetString() ?? string.Empty);
        return RequireNumber(value).GetDouble();
    }

    private static float ParseSingle(string text) => text switch
    {
        "NaN" => float.NaN,
        "Infinity" or "+Infinity" => float.PositiveInfinity,
        "-Infinity" => float.NegativeInfinity,
        _ => throw new JsonException("Attribute JSON single values require a numeric 'value' or 'NaN', 'Infinity', or '-Infinity'."),
    };

    private static double ParseDouble(string text) => text switch
    {
        "NaN" => double.NaN,
        "Infinity" or "+Infinity" => double.PositiveInfinity,
        "-Infinity" => double.NegativeInfinity,
        _ => throw new JsonException("Attribute JSON double values require a numeric 'value' or 'NaN', 'Infinity', or '-Infinity'."),
    };

    private static string NonFiniteName(float value) =>
        float.IsNaN(value) ? "NaN" : float.IsNegativeInfinity(value) ? "-Infinity" : "Infinity";

    private static string NonFiniteName(double value) =>
        double.IsNaN(value) ? "NaN" : double.IsNegativeInfinity(value) ? "-Infinity" : "Infinity";

    private static char ReadChar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetUInt16(out var codeUnit))
                return (char)codeUnit;
            throw new JsonException("Attribute JSON char values require a UTF-16 code unit (0-65535) or single-character 'value'.");
        }

        var text = RequireString(value);
        return text.Length == 1 ? text[0] : throw new JsonException("Attribute JSON char values require a UTF-16 code unit (0-65535) or single-character 'value'.");
    }

    private static object? InferUntypedPrimitive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => throw new JsonException("Attribute JSON primitive values require a boolean, number, or string 'value'."),
    };

    private static JsonElement RequireNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
            ? value
            : throw new JsonException("Attribute JSON numeric values require a numeric 'value'.");

    private static JsonElement RequireValueKind(JsonElement value, params JsonValueKind[] kinds) =>
        kinds.Contains(value.ValueKind)
            ? value
            : throw new JsonException("Attribute JSON boolean values require a boolean 'value'.");
}
