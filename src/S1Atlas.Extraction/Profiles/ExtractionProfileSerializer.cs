using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

internal sealed class ExtractionProfileSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ExtractionProfileValidator _validator = new();

    public ResolvedExtractionProfile Deserialize(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        ExtractionProfileDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ExtractionProfileDocument>(json, JsonOptions)
                ?? throw new JsonException("The document contains no JSON value.");
        }
        catch (JsonException exception)
        {
            throw InvalidJson(sourceName, exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidJson(sourceName, exception);
        }

        var profile = _validator.Validate(document, sourceName);
        return new ResolvedExtractionProfile(profile, ExtractionProfileFingerprint.Create(profile));
    }

    private static ToolOperationException InvalidJson(string sourceName, Exception exception) =>
        new("ExtractionProfileInvalid", $"Extraction profile '{sourceName}' is not valid strict JSON: {exception.Message}", exception);
}
