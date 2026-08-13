using System.Text.RegularExpressions;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

internal sealed class ExtractionProfileValidator
{
    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RolePattern = new(
        "^[a-z][A-Za-z0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ExtractionProfile Validate(ExtractionProfileDocument document, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        RequireExact(document.SchemaVersion, 1, sourceName, "schemaVersion");
        var profileId = RequireId(document.ProfileId, sourceName, "profileId");
        RequireExact(document.ProfileVersion, 1, sourceName, "profileVersion");
        RequireExact(document.AdapterVersion, 1, sourceName, "adapterVersion");
        RequireExact(document.ExtractionSchemaVersion, 1, sourceName, "extractionSchemaVersion");
        var executableName = RequireString(document.ExecutableName, sourceName, "executableName");
        var outputFormat = RequireString(document.OutputFormat, sourceName, "outputFormat");
        RequireExact(document.TimeoutSeconds, 1800, sourceName, "timeoutSeconds");
        var maximumOutputBytes = RequirePositive(document.MaximumRetainedStandardOutputBytes, sourceName, "maximumRetainedStandardOutputBytes");
        var maximumErrorBytes = RequirePositive(document.MaximumRetainedStandardErrorBytes, sourceName, "maximumRetainedStandardErrorBytes");
        var acceptedExitCodes = RequireDistinctIntegers(document.AcceptedExitCodes, sourceName, "acceptedExitCodes");
        var identities = RequireDistinctStrings(document.RequiredAssemblyIdentities, sourceName, "requiredAssemblyIdentities");
        var snapshotInputs = ValidateSnapshotInputs(document.SnapshotInputs, sourceName);
        var unityVersionSources = RequireDistinctContainedPaths(document.UnityVersionSources, sourceName, "unityVersionSources");

        if (profileId == "cpp2il-reconstructed-assemblies-v1" &&
            (!string.Equals(executableName, "Schedule I", StringComparison.Ordinal) ||
             !string.Equals(outputFormat, "dll_il_recovery", StringComparison.Ordinal)))
        {
            throw Invalid(sourceName, "profile", "must use the approved Schedule I dll_il_recovery values.");
        }

        return new ExtractionProfile(
            document.SchemaVersion!.Value, profileId, document.ProfileVersion!.Value,
            document.AdapterVersion!.Value, document.ExtractionSchemaVersion!.Value,
            executableName, outputFormat, TimeSpan.FromSeconds(document.TimeoutSeconds!.Value),
            maximumOutputBytes, maximumErrorBytes, acceptedExitCodes, identities,
            snapshotInputs, unityVersionSources);
    }

    private static IReadOnlyList<SnapshotInputDefinition> ValidateSnapshotInputs(List<SnapshotInputDocument?>? values, string sourceName)
    {
        if (values is null || values.Count == 0) throw Invalid(sourceName, "snapshotInputs", "must contain at least one value.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SnapshotInputDefinition>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index] ?? throw Invalid(sourceName, $"snapshotInputs[{index}]", "cannot be null.");
            var path = RequireContainedRelativePath(item.RelativePath, sourceName, $"snapshotInputs[{index}].relativePath");
            var role = RequireString(item.Role, sourceName, $"snapshotInputs[{index}].role");
            if (!RolePattern.IsMatch(role)) throw Invalid(sourceName, $"snapshotInputs[{index}].role", "must be a safe camel-case role.");
            if (!paths.Add(path) || !roles.Add(role)) throw Invalid(sourceName, $"snapshotInputs[{index}]", "must have ordinal-unique paths and roles.");
            result.Add(new SnapshotInputDefinition(path, role));
        }
        return result;
    }

    private static IReadOnlyList<int> RequireDistinctIntegers(List<int>? values, string sourceName, string fieldName)
    {
        if (values is null || values.Count == 0) throw Invalid(sourceName, fieldName, "must contain at least one value.");
        if (values.Distinct().Count() != values.Count) throw Invalid(sourceName, fieldName, "must not contain duplicates.");
        return values.ToArray();
    }

    private static IReadOnlyList<string> RequireDistinctStrings(List<string?>? values, string sourceName, string fieldName)
    {
        if (values is null || values.Count == 0) throw Invalid(sourceName, fieldName, "must contain at least one value.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var required = RequireString(value, sourceName, fieldName);
            if (!seen.Add(required)) throw Invalid(sourceName, fieldName, "must be ordinal-unique.");
            result.Add(required);
        }
        return result;
    }

    private static IReadOnlyList<string> RequireDistinctContainedPaths(List<string?>? values, string sourceName, string fieldName)
    {
        if (values is null || values.Count == 0) throw Invalid(sourceName, fieldName, "must contain at least one value.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var path = RequireContainedRelativePath(value, sourceName, fieldName);
            if (!seen.Add(path)) throw Invalid(sourceName, fieldName, "must be ordinal-unique.");
            result.Add(path);
        }
        return result;
    }

    private static string RequireId(string? value, string sourceName, string fieldName)
    {
        var id = RequireString(value, sourceName, fieldName);
        if (!IdPattern.IsMatch(id)) throw Invalid(sourceName, fieldName, "must be a lower-case safe identifier.");
        return id;
    }

    private static string RequireContainedRelativePath(string? value, string sourceName, string fieldName)
    {
        var path = RequireString(value, sourceName, fieldName);
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Any(char.IsControl)) throw Invalid(sourceName, fieldName, "must be a contained relative path.");
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..")) throw Invalid(sourceName, fieldName, "cannot contain empty, '.', or '..' segments.");
        return string.Join('/', segments);
    }

    private static string RequireString(string? value, string sourceName, string fieldName) =>
        string.IsNullOrWhiteSpace(value) ? throw Invalid(sourceName, fieldName, "is required and cannot be blank.") : value;

    private static long RequirePositive(long? value, string sourceName, string fieldName) =>
        value is null or <= 0 ? throw Invalid(sourceName, fieldName, "must be positive.") : value.Value;

    private static void RequireExact(int? value, int expected, string sourceName, string fieldName)
    {
        if (value != expected) throw Invalid(sourceName, fieldName, $"must be exactly {expected}.");
    }

    private static ToolOperationException Invalid(string sourceName, string fieldName, string message) =>
        new("ExtractionProfileInvalid", $"Extraction profile '{sourceName}' field '{fieldName}' {message}");
}
