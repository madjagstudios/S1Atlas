using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolDefinitionSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ToolDefinitionValidator _validator = new();

    public ResolvedToolDefinition Deserialize(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        ToolDefinitionDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ToolDefinitionDocument>(
                    json,
                    JsonOptions) ??
                throw new JsonException("The document contains no JSON value.");
        }
        catch (JsonException exception)
        {
            throw InvalidJson(sourceName, exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidJson(sourceName, exception);
        }

        var definition = _validator.Validate(document, sourceName);
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }

    public string Serialize(ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var document = new ToolDefinitionDocument
        {
            SchemaVersion = definition.SchemaVersion,
            ToolId = definition.ToolId,
            DisplayName = definition.DisplayName,
            Version = definition.Version,
            Platform = definition.Platform,
            Package = new ToolPackageDocument
            {
                Kind = definition.Package.Kind switch
                {
                    ToolPackageKind.SingleFile => "singleFile",
                    ToolPackageKind.Archive => "archive",
                    _ => throw new ToolOperationException(
                        "ToolDefinitionInvalid",
                        "The tool package kind cannot be serialized.")
                },
                ArchiveFormat = definition.Package.ArchiveFormat switch
                {
                    null => null,
                    ToolArchiveFormat.Zip => "zip",
                    _ => throw new ToolOperationException(
                        "ToolDefinitionInvalid",
                        "The tool archive format cannot be serialized.")
                },
                SourceUrl = definition.Package.SourceUri.AbsoluteUri,
                ReleaseUrl = definition.Package.ReleaseUri.AbsoluteUri,
                AssetName = definition.Package.AssetName,
                ExpectedSize = definition.Package.ExpectedSize,
                Sha256 = definition.Package.Sha256,
                ExecutableRelativePath =
                    definition.Package.ExecutableRelativePath,
                Limits = new ToolSafetyLimitsDocument
                {
                    MaximumDownloadBytes =
                        definition.Package.Limits.MaximumDownloadBytes,
                    MaximumExpandedBytes =
                        definition.Package.Limits.MaximumExpandedBytes,
                    MaximumFileCount =
                        definition.Package.Limits.MaximumFileCount
                }
            },
            License = new ToolLicenseDocument
            {
                SpdxIdentifier = definition.License.SpdxIdentifier,
                SourceUrl = definition.License.SourceUri.AbsoluteUri
            },
            Probes = definition.Probes
                .Select(probe => new ToolProbeDocument
                {
                    ProbeId = probe.ProbeId,
                    Arguments = probe.Arguments
                        .Select(argument => (string?)argument)
                        .ToList(),
                    AcceptedExitCodes = probe.AcceptedExitCodes.ToList(),
                    TimeoutSeconds = GetWholeTimeoutSeconds(probe),
                    RequiredOutputSubstrings = probe.RequiredOutputSubstrings
                        .Select(value => (string?)value)
                        .ToList()
                })
                .Select(probe => (ToolProbeDocument?)probe)
                .ToList()
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static int GetWholeTimeoutSeconds(ToolProbeDefinition probe)
    {
        if (probe.Timeout.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ToolOperationException(
                "ToolDefinitionInvalid",
                $"Probe '{probe.ProbeId}' timeout must be a whole number of seconds.");
        }

        return checked((int)(probe.Timeout.Ticks / TimeSpan.TicksPerSecond));
    }

    private static ToolOperationException InvalidJson(
        string sourceName,
        Exception innerException) =>
        new(
            "ToolDefinitionInvalid",
            $"Tool definition '{sourceName}' is not valid strict JSON: {innerException.Message}",
            innerException);
}
