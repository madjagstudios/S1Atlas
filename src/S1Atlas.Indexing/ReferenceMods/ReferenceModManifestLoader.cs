using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S1Atlas.Core.ReferenceMods;

namespace S1Atlas.Indexing.ReferenceMods;

public sealed class ReferenceModManifestLoader
{
    private const int MaximumManifestBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly Regex StableIdPattern = new(
        "^[a-z0-9][a-z0-9-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<ReferenceCollectionDefinition> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw Invalid("manifest", "does not exist.");
        }

        ReferenceModManifestDocument document;
        try
        {
            var json = await ReadBoundedTextAsync(fullManifestPath, cancellationToken);
            ValidateShape(json);
            document = JsonSerializer.Deserialize<ReferenceModManifestDocument>(
                json,
                JsonOptions) ?? throw Invalid("manifest", "is empty.");
        }
        catch (JsonException exception)
        {
            throw Invalid("manifest", $"is not valid JSON: {exception.Message}");
        }

        var collectionId = NormalizeStableId(document.Collection, "collection");
        var mods = RequireMods(document.Mods);

        var seenModIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedMods = new List<ReferenceModDefinition>(mods.Count);
        for (var index = 0; index < mods.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"mods[{index}]";
            var mod = mods[index] ?? throw Invalid(path, "cannot be null.");
            var modId = NormalizeStableId(mod.Id, $"{path}.id");
            if (!seenModIds.Add(modId))
            {
                throw Invalid($"{path}.id", "must be unique after normalization.");
            }

            var displayName = RequireNonBlank(mod.DisplayName, $"{path}.displayName");
            var version = RequireNonBlank(mod.Version, $"{path}.version");
            var license = RequireNonBlank(mod.License, $"{path}.license");
            var rootPath = NormalizeRootPath(mod.RootPath, $"{path}.rootPath");
            var include = NormalizePatterns(mod.Include, $"{path}.include");
            var exclude = NormalizePatterns(mod.Exclude, $"{path}.exclude", allowEmpty: true);

            normalizedMods.Add(new ReferenceModDefinition(
                modId,
                displayName,
                version,
                license,
                rootPath,
                string.Empty,
                include,
                exclude));
        }

        normalizedMods.Sort((left, right) => StringComparer.Ordinal.Compare(left.ModId, right.ModId));
        return new ReferenceCollectionDefinition(
            string.Empty,
            string.Empty,
            normalizedMods,
            collectionId,
            string.IsNullOrWhiteSpace(document.CollectionName)
                ? null
                : document.CollectionName);
    }

    private static string NormalizeStableId(string? value, string fieldName)
    {
        var normalized = RequireNonBlank(value, fieldName).ToLowerInvariant();
        if (!StableIdPattern.IsMatch(normalized))
        {
            throw Invalid(fieldName, "must contain only lower-case letters, digits, or hyphens.");
        }

        return normalized;
    }

    private static string RequireNonBlank(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(fieldName, "is required and cannot be blank.");
        }

        return value;
    }

    private static List<ReferenceModDocument?> RequireMods(List<ReferenceModDocument?>? mods)
    {
        if (mods is null || mods.Count == 0)
        {
            throw Invalid("mods", "must contain at least one mod.");
        }

        return mods;
    }

    private static IReadOnlyList<string> NormalizePatterns(
        List<string?>? values,
        string fieldName,
        bool allowEmpty = false)
    {
        if (values is null)
        {
            return allowEmpty ? [] : throw Invalid(fieldName, "is required.");
        }

        if (!allowEmpty && values.Count == 0)
        {
            throw Invalid(fieldName, "must contain at least one include pattern.");
        }

        var normalized = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index];
            var itemPath = $"{fieldName}[{index}]";
            if (string.IsNullOrWhiteSpace(item))
            {
                throw Invalid(itemPath, "cannot be blank.");
            }

            var canonical = ReferenceModGlob.Normalize(item, itemPath);
            normalized.Add(canonical);
        }

        normalized.Sort(StringComparer.Ordinal);
        return normalized;
    }

    private static string NormalizeRootPath(string? value, string fieldName)
    {
        var rootPath = RequireNonBlank(value, fieldName);
        if (LooksLikeNonLocalUri(rootPath))
        {
            throw Invalid(fieldName, "must be a local filesystem path, not a URL.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid(fieldName, "is not a valid local filesystem path.");
        }

        if (!Path.IsPathRooted(rootPath) ||
            fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            !ReferenceModPathSafety.IsNormalDirectory(fullPath))
        {
            throw Invalid(fieldName, "must resolve to an existing local directory.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool LooksLikeNonLocalUri(string value)
    {
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Scheme) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
            !(uri.Scheme.Length == 1 && char.IsLetter(uri.Scheme[0]));
    }

    private static async Task<string> ReadBoundedTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumManifestBytes)
        {
            throw Invalid("manifest", $"exceeds the {MaximumManifestBytes}-byte local limit.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static void ValidateShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("manifest", "must be a JSON object.");
        }

        RequireOnlyProperties(root, "manifest", ["collection", "collectionName", "mods"]);
        if (root.TryGetProperty("mods", out var modsElement))
        {
            if (modsElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("mods", "must be an array.");
            }

            var index = 0;
            foreach (var mod in modsElement.EnumerateArray())
            {
                if (mod.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid($"mods[{index}]", "must be a JSON object.");
                }

                RequireOnlyProperties(
                    mod,
                    $"mods[{index}]",
                    ["id", "displayName", "rootPath", "version", "license", "include", "exclude"]);
                index++;
            }
        }
    }

    private static void RequireOnlyProperties(
        JsonElement element,
        string fieldName,
        IReadOnlyList<string> allowedProperties)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid($"{fieldName}.{property.Name}", "is not recognized.");
            }
        }
    }

    private static InvalidDataException Invalid(string fieldName, string message) =>
        new($"Reference mod manifest field '{fieldName}' {message}");

    private sealed class ReferenceModManifestDocument
    {
        public string? Collection { get; init; }
        public string? CollectionName { get; init; }
        public List<ReferenceModDocument?>? Mods { get; init; }
    }

    private sealed class ReferenceModDocument
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? RootPath { get; init; }
        public string? Version { get; init; }
        public string? License { get; init; }
        public List<string?>? Include { get; init; }
        public List<string?>? Exclude { get; init; }
    }
}
