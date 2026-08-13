using System.Text.RegularExpressions;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolDefinitionValidator
{
    private static readonly Regex ToolIdPattern = new(
        "^[a-z0-9][a-z0-9.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex PlatformPattern = new(
        "^[a-z0-9][a-z0-9-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly char[] UnsafePathSegmentCharacters =
    [
        '/', '\\', ':', '<', '>', '"', '|', '?', '*'
    ];

    public ToolDefinition Validate(
        ToolDefinitionDocument document,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (document.SchemaVersion != 1)
        {
            throw Invalid(
                sourceName,
                "schemaVersion",
                "must be exactly 1.");
        }

        var toolId = RequireString(document.ToolId, sourceName, "toolId");
        if (!ToolIdPattern.IsMatch(toolId))
        {
            throw Invalid(
                sourceName,
                "toolId",
                "must contain only lower-case letters, digits, dots, or hyphens and begin with a letter or digit.");
        }

        var displayName = RequireString(
            document.DisplayName,
            sourceName,
            "displayName");
        var version = RequireSafeSegment(
            document.Version,
            sourceName,
            "version");
        var platform = RequireString(
            document.Platform,
            sourceName,
            "platform");
        if (!PlatformPattern.IsMatch(platform))
        {
            throw Invalid(
                sourceName,
                "platform",
                "must contain only lower-case letters, digits, or hyphens and begin with a letter or digit.");
        }

        var package = ValidatePackage(
            document.Package ?? throw Invalid(
                sourceName,
                "package",
                "is required."),
            sourceName);
        var license = ValidateLicense(
            document.License ?? throw Invalid(
                sourceName,
                "license",
                "is required."),
            sourceName);
        var probes = ValidateProbes(document.Probes, sourceName);

        return new ToolDefinition(
            document.SchemaVersion.Value,
            toolId,
            displayName,
            version,
            platform,
            package,
            license,
            probes);
    }

    private static ToolPackageDefinition ValidatePackage(
        ToolPackageDocument document,
        string sourceName)
    {
        var kindText = RequireString(
            document.Kind,
            sourceName,
            "package.kind");
        var kind = kindText switch
        {
            "singleFile" => ToolPackageKind.SingleFile,
            "archive" => ToolPackageKind.Archive,
            _ => throw Invalid(
                sourceName,
                "package.kind",
                "must be exactly 'singleFile' or 'archive'.")
        };

        ToolArchiveFormat? archiveFormat = document.ArchiveFormat switch
        {
            null => null,
            "zip" => ToolArchiveFormat.Zip,
            _ => throw Invalid(
                sourceName,
                "package.archiveFormat",
                "must be null or exactly 'zip'.")
        };

        if (kind == ToolPackageKind.SingleFile && archiveFormat is not null)
        {
            throw Invalid(
                sourceName,
                "package.archiveFormat",
                "must be null for a single-file package.");
        }

        if (kind == ToolPackageKind.Archive && archiveFormat != ToolArchiveFormat.Zip)
        {
            throw Invalid(
                sourceName,
                "package.archiveFormat",
                "must be 'zip' for an archive package.");
        }

        var sourceUri = RequireHttpsUri(
            document.SourceUrl,
            sourceName,
            "package.sourceUrl");
        var releaseUri = RequireHttpsUri(
            document.ReleaseUrl,
            sourceName,
            "package.releaseUrl");
        var assetName = RequireSafeSegment(
            document.AssetName,
            sourceName,
            "package.assetName");
        var sourceAssetName = GetFinalUriSegment(sourceUri);
        if (!string.Equals(sourceAssetName, assetName, StringComparison.Ordinal))
        {
            throw Invalid(
                sourceName,
                "package.assetName",
                "must exactly match the final source URL path segment.");
        }

        if (document.ExpectedSize is null or <= 0)
        {
            throw Invalid(
                sourceName,
                "package.expectedSize",
                "must be positive.");
        }

        var sha256 = RequireString(
            document.Sha256,
            sourceName,
            "package.sha256");
        if (!Sha256Pattern.IsMatch(sha256))
        {
            throw Invalid(
                sourceName,
                "package.sha256",
                "must contain exactly 64 hexadecimal characters.");
        }

        var executableRelativePath = RequireContainedRelativePath(
            document.ExecutableRelativePath,
            sourceName,
            "package.executableRelativePath");
        var limitsDocument = document.Limits ?? throw Invalid(
            sourceName,
            "package.limits",
            "is required.");
        var maximumDownloadBytes = RequirePositive(
            limitsDocument.MaximumDownloadBytes,
            sourceName,
            "package.limits.maximumDownloadBytes");
        var maximumExpandedBytes = RequirePositive(
            limitsDocument.MaximumExpandedBytes,
            sourceName,
            "package.limits.maximumExpandedBytes");
        var maximumFileCount = RequirePositive(
            limitsDocument.MaximumFileCount,
            sourceName,
            "package.limits.maximumFileCount");

        if (document.ExpectedSize.Value > maximumDownloadBytes)
        {
            throw Invalid(
                sourceName,
                "package.expectedSize",
                "cannot exceed package.limits.maximumDownloadBytes.");
        }

        if (kind == ToolPackageKind.SingleFile && maximumFileCount != 1)
        {
            throw Invalid(
                sourceName,
                "package.limits.maximumFileCount",
                "must be exactly 1 for a single-file package.");
        }

        return new ToolPackageDefinition(
            kind,
            archiveFormat,
            sourceUri,
            releaseUri,
            assetName,
            document.ExpectedSize.Value,
            sha256.ToLowerInvariant(),
            executableRelativePath,
            new ToolSafetyLimits(
                maximumDownloadBytes,
                maximumExpandedBytes,
                maximumFileCount));
    }

    private static ToolLicenseDefinition ValidateLicense(
        ToolLicenseDocument document,
        string sourceName)
    {
        return new ToolLicenseDefinition(
            RequireString(
                document.SpdxIdentifier,
                sourceName,
                "license.spdxIdentifier"),
            RequireHttpsUri(
                document.SourceUrl,
                sourceName,
                "license.sourceUrl"));
    }

    private static IReadOnlyList<ToolProbeDefinition> ValidateProbes(
        List<ToolProbeDocument?>? documents,
        string sourceName)
    {
        if (documents is null || documents.Count == 0)
        {
            throw Invalid(
                sourceName,
                "probes",
                "must contain at least one probe.");
        }

        var probeIds = new HashSet<string>(StringComparer.Ordinal);
        var probes = new List<ToolProbeDefinition>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var path = $"probes[{index}]";
            var document = documents[index] ?? throw Invalid(
                sourceName,
                path,
                "cannot be null.");
            var probeId = RequireString(
                document.ProbeId,
                sourceName,
                $"{path}.probeId");
            if (!probeIds.Add(probeId))
            {
                throw Invalid(
                    sourceName,
                    $"{path}.probeId",
                    "must be unique using ordinal comparison.");
            }

            var arguments = RequireStringCollection(
                document.Arguments,
                sourceName,
                $"{path}.arguments");
            if (document.AcceptedExitCodes is null ||
                document.AcceptedExitCodes.Count == 0)
            {
                throw Invalid(
                    sourceName,
                    $"{path}.acceptedExitCodes",
                    "must contain at least one value.");
            }

            if (document.AcceptedExitCodes.Distinct().Count() !=
                document.AcceptedExitCodes.Count)
            {
                throw Invalid(
                    sourceName,
                    $"{path}.acceptedExitCodes",
                    "cannot contain duplicate values.");
            }

            if (document.TimeoutSeconds is null or < 1 or > 300)
            {
                throw Invalid(
                    sourceName,
                    $"{path}.timeoutSeconds",
                    "must be between 1 and 300 seconds.");
            }

            var requiredOutput = RequireStringCollection(
                document.RequiredOutputSubstrings,
                sourceName,
                $"{path}.requiredOutputSubstrings");
            probes.Add(new ToolProbeDefinition(
                probeId,
                arguments,
                document.AcceptedExitCodes.ToArray(),
                TimeSpan.FromSeconds(document.TimeoutSeconds.Value),
                requiredOutput));
        }

        return probes;
    }

    private static IReadOnlyList<string> RequireStringCollection(
        List<string?>? values,
        string sourceName,
        string fieldName)
    {
        if (values is null)
        {
            throw Invalid(sourceName, fieldName, "is required.");
        }

        if (values.Any(value => value is null))
        {
            throw Invalid(sourceName, fieldName, "cannot contain null values.");
        }

        return values.Select(value => value!).ToArray();
    }

    private static string RequireString(
        string? value,
        string sourceName,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(sourceName, fieldName, "is required and cannot be blank.");
        }

        return value;
    }

    private static string RequireSafeSegment(
        string? value,
        string sourceName,
        string fieldName)
    {
        var segment = RequireString(value, sourceName, fieldName);
        if (segment is "." or ".." ||
            segment.IndexOfAny(UnsafePathSegmentCharacters) >= 0 ||
            segment.Any(char.IsControl) ||
            Path.GetFileName(segment) != segment)
        {
            throw Invalid(
                sourceName,
                fieldName,
                "must be one safe filename/path segment.");
        }

        return segment;
    }

    private static string RequireContainedRelativePath(
        string? value,
        string sourceName,
        string fieldName)
    {
        var relativePath = RequireString(value, sourceName, fieldName);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') ||
            relativePath.Any(char.IsControl))
        {
            throw Invalid(
                sourceName,
                fieldName,
                "must be a contained relative path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw Invalid(
                sourceName,
                fieldName,
                "cannot contain empty, '.', or '..' path segments.");
        }

        return string.Join('/', segments);
    }

    private static Uri RequireHttpsUri(
        string? value,
        string sourceName,
        string fieldName)
    {
        var uriText = RequireString(value, sourceName, fieldName);
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid(
                sourceName,
                fieldName,
                "must be an absolute credential-free HTTPS URL.");
        }

        return uri;
    }

    private static string GetFinalUriSegment(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var segments = uri.Segments;
        return segments.Length == 0
            ? string.Empty
            : Uri.UnescapeDataString(segments[^1]);
    }

    private static long RequirePositive(
        long? value,
        string sourceName,
        string fieldName)
    {
        if (value is null or <= 0)
        {
            throw Invalid(sourceName, fieldName, "must be positive.");
        }

        return value.Value;
    }

    private static int RequirePositive(
        int? value,
        string sourceName,
        string fieldName)
    {
        if (value is null or <= 0)
        {
            throw Invalid(sourceName, fieldName, "must be positive.");
        }

        return value.Value;
    }

    private static ToolOperationException Invalid(
        string sourceName,
        string fieldName,
        string message) =>
        new(
            "ToolDefinitionInvalid",
            $"Tool definition '{sourceName}' field '{fieldName}' {message}");
}
