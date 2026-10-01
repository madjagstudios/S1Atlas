using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace S1Atlas.Application.Envelope;

// Builds the JSON options shared by the MCP tools and the local web API so
// both serialize envelopes identically: camelCase names, null properties
// omitted, enums as member names, tool status and provenance via converters.
public static class ToolJsonOptions
{
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
            Modifiers = { SkipNullProperties }
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
}
