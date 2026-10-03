using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Application.Envelope;

// Builds the JSON options shared by the MCP tools and the local web API so
// both serialize envelopes identically: camelCase names, null properties
// omitted, enums as member names, tool status and provenance via converters.
// Codebase and origin fields serialize with the spellings the MCP inputs
// advertise, so clients see one vocabulary on the wire. Records keep their
// stored spellings, so CLI output is unchanged.
public static class ToolJsonOptions
{
    private static readonly IReadOnlyDictionary<string, string> CodebaseSpellings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(CodebaseKind.ScheduleI)] = "scheduleI",
            [nameof(CodebaseKind.S1Api)] = "s1api",
            [nameof(CodebaseKind.S1MApi)] = "s1mapi"
        };

    private static readonly IReadOnlyDictionary<string, string> OriginSpellings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game"] = "Game",
            ["reference"] = "Reference"
        };

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { SkipNullProperties, SkipEmptyCandidatesAndSuggestions, MapAdvertisedVocabulary, MapErrorCodes }
        };
        options.Converters.Add(new ToolStatusJsonConverter());
        options.Converters.Add(new ProvenanceClassificationJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal static void SkipNullProperties(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
            property.ShouldSerialize = (_, value) => value is not null;
    }

    public static void SkipEmptyCandidatesAndSuggestions(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (!property.Name.Equals("candidates", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("suggestions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prior = property.ShouldSerialize;
            property.ShouldSerialize = (parent, value) =>
                (prior is null || prior(parent, value)) &&
                value is not System.Collections.ICollection { Count: 0 };
        }
    }

    public static void MapAdvertisedVocabulary(JsonTypeInfo typeInfo)
    {
        // JsonPropertyInfo.Name already carries the camelCase wire name when
        // modifiers run, so match it case-insensitively.
        foreach (var property in typeInfo.Properties)
        {
            if (property.Name.Equals("Codebase", StringComparison.OrdinalIgnoreCase) && property.PropertyType == typeof(string))
                property.CustomConverter = new VocabularyJsonConverter(CodebaseSpellings);
            else if (property.Name.Equals("Codebase", StringComparison.OrdinalIgnoreCase) && property.PropertyType == typeof(CodebaseKind))
                property.CustomConverter = new CodebaseKindJsonConverter();
            else if (property.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase) && property.PropertyType == typeof(string))
                property.CustomConverter = new VocabularyJsonConverter(OriginSpellings);
        }
    }

    public static void MapErrorCodes(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ToolError))
            return;

        foreach (var property in typeInfo.Properties)
        {
            if (property.Name.Equals("code", StringComparison.OrdinalIgnoreCase) &&
                property.PropertyType == typeof(string))
            {
                property.CustomConverter = new ToolErrorCodeJsonConverter();
            }
        }
    }

    private sealed class ToolErrorCodeJsonConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(McpToolErrorCodes.MapToWireCode(value));
    }

    private sealed class VocabularyJsonConverter(IReadOnlyDictionary<string, string> spellings) : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(spellings.TryGetValue(value, out var mapped) ? mapped : value);
    }

    private sealed class CodebaseKindJsonConverter : JsonConverter<CodebaseKind>
    {
        public override CodebaseKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (text is not null)
            {
                foreach (var (member, advertised) in CodebaseSpellings)
                {
                    if (string.Equals(text, advertised, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, member, StringComparison.OrdinalIgnoreCase))
                        return (CodebaseKind)Enum.Parse(typeof(CodebaseKind), member);
                }

                if (Enum.TryParse<CodebaseKind>(text, ignoreCase: true, out var parsed))
                    return parsed;
            }

            throw new JsonException($"Unknown codebase '{text}'.");
        }

        public override void Write(Utf8JsonWriter writer, CodebaseKind value, JsonSerializerOptions options)
        {
            var member = value.ToString();
            writer.WriteStringValue(CodebaseSpellings.TryGetValue(member, out var mapped) ? mapped : member);
        }
    }
}
