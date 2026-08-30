using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using S1Atlas.Application.Envelope;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Mcp.Serialization;

internal sealed class SeamToolEnvelopeJsonConverter : JsonConverter<ToolEnvelope<SeamInvestigationResult>>
{
    public override ToolEnvelope<SeamInvestigationResult> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("MCP seam envelopes are write-only.");

    public override void Write(
        Utf8JsonWriter writer,
        ToolEnvelope<SeamInvestigationResult> value,
        JsonSerializerOptions options)
    {
        var seamOptions = new JsonSerializerOptions(options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        foreach (var property in typeInfo.Properties)
                        {
                            property.ShouldSerialize = (instance, _) =>
                                !property.Name.Equals(nameof(SeamInvestigationResult.NativeEvidence), StringComparison.OrdinalIgnoreCase) ||
                                instance is not SeamInvestigationResult { NativeEvidence: null };
                        }

                        if (typeInfo.Type == typeof(SeamEvidenceClaim))
                        {
                            var evidenceClassification = typeInfo.Properties.FirstOrDefault(
                                property => property.Name == "evidenceClassification");
                            if (evidenceClassification is not null)
                                typeInfo.Properties.Remove(evidenceClassification);
                        }
                    }
                }
            }
        };

        for (var index = seamOptions.Converters.Count - 1; index >= 0; index--)
        {
            if (seamOptions.Converters[index] is SeamToolEnvelopeJsonConverter)
                seamOptions.Converters.RemoveAt(index);
        }

        JsonSerializer.Serialize(writer, value, seamOptions);
    }
}
