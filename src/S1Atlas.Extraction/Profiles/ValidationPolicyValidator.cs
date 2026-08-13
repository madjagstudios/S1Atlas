using System.Text.RegularExpressions;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

internal sealed class ValidationPolicyValidator
{
    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ValidationPolicy Validate(ValidationPolicyDocument document, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        RequireExact(document.SchemaVersion, 1, sourceName, "schemaVersion");
        var policyId = RequireId(document.PolicyId, sourceName, "policyId");
        RequireExact(document.PolicyVersion, 1, sourceName, "policyVersion");
        var identities = RequireDistinctStrings(document.RequiredAssemblyIdentities, sourceName, "requiredAssemblyIdentities");
        var minimumAssemblyCount = RequirePositive(document.MinimumManagedAssemblyCount, sourceName, "minimumManagedAssemblyCount");
        var minimumTypeCount = RequirePositive(document.MinimumTypeDefinitionCount, sourceName, "minimumTypeDefinitionCount");
        var minimumMethodCount = RequirePositive(document.MinimumMethodDefinitionCount, sourceName, "minimumMethodDefinitionCount");
        var minimumBytes = RequirePositive(document.MinimumTotalManagedBytes, sourceName, "minimumTotalManagedBytes");
        var warning = RequireRatio(document.ComparativeWarningRelativeChange, sourceName, "comparativeWarningRelativeChange");
        var catastrophic = RequireRatio(document.CatastrophicDecreaseRelativeChange, sourceName, "catastrophicDecreaseRelativeChange");
        if (catastrophic <= warning) throw Invalid(sourceName, "catastrophicDecreaseRelativeChange", "must be greater than comparativeWarningRelativeChange.");

        return new ValidationPolicy(document.SchemaVersion!.Value, policyId, document.PolicyVersion!.Value,
            identities, minimumAssemblyCount, minimumTypeCount, minimumMethodCount, minimumBytes, warning, catastrophic);
    }

    private static IReadOnlyList<string> RequireDistinctStrings(List<string?>? values, string sourceName, string fieldName)
    {
        if (values is null || values.Count == 0) throw Invalid(sourceName, fieldName, "must contain at least one value.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) throw Invalid(sourceName, fieldName, "cannot contain blank values.");
            if (!seen.Add(value)) throw Invalid(sourceName, fieldName, "must be ordinal-unique.");
            result.Add(value);
        }
        return result;
    }

    private static string RequireId(string? value, string sourceName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdPattern.IsMatch(value)) throw Invalid(sourceName, fieldName, "must be a lower-case safe identifier.");
        return value;
    }

    private static int RequirePositive(int? value, string sourceName, string fieldName) =>
        value is null or <= 0 ? throw Invalid(sourceName, fieldName, "must be positive.") : value.Value;

    private static long RequirePositive(long? value, string sourceName, string fieldName) =>
        value is null or <= 0 ? throw Invalid(sourceName, fieldName, "must be positive.") : value.Value;

    private static double RequireRatio(double? value, string sourceName, string fieldName) =>
        value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value <= 0 || value >= 1
            ? throw Invalid(sourceName, fieldName, "must be strictly between 0 and 1.")
            : value.Value;

    private static void RequireExact(int? value, int expected, string sourceName, string fieldName)
    {
        if (value != expected) throw Invalid(sourceName, fieldName, $"must be exactly {expected}.");
    }

    private static ToolOperationException Invalid(string sourceName, string fieldName, string message) =>
        new("ValidationPolicyInvalid", $"Validation policy '{sourceName}' field '{fieldName}' {message}");
}
