using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

internal sealed class ValidationPolicySerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ValidationPolicyValidator _validator = new();

    public ResolvedValidationPolicy Deserialize(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        ValidationPolicyDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ValidationPolicyDocument>(json, JsonOptions)
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

        var policy = _validator.Validate(document, sourceName);
        return new ResolvedValidationPolicy(policy, ValidationPolicyFingerprint.Create(policy));
    }

    private static ToolOperationException InvalidJson(string sourceName, Exception exception) =>
        new("ValidationPolicyInvalid", $"Validation policy '{sourceName}' is not valid strict JSON: {exception.Message}", exception);
}
