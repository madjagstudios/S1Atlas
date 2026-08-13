using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// The outcome of <see cref="ComparativeSanityValidator.Validate"/>: every major
/// metric's <see cref="ValidationMetricComparison"/>, in deterministic order, and
/// the issues (if any) those comparisons produced.
/// </summary>
internal sealed record ComparativeSanityResult(
    IReadOnlyList<ValidationMetricComparison> Comparisons,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Compares a candidate's statistics against a preferred baseline's statistics
/// using the committed policy's comparative-warning and catastrophic-decrease
/// relative-change thresholds:
/// <code>relative change = |candidate - baseline| / baseline</code>
/// Hard-failure checks run first: a major count that <em>decreases</em> by more
/// than the policy's catastrophic threshold is a preference-blocking
/// <c>CatastrophicSanityDeviation</c> error, regardless of the warning rule below.
/// Otherwise, a major count that changes by more than the policy's comparative
/// threshold in either direction is a <c>ComparativeSanityDeviation</c> warning.
/// Exactly the threshold value is not "more than" it, so it produces neither
/// issue. A zero baseline is never divided by; a nonzero candidate against a zero
/// baseline is a <c>BaselineZeroChanged</c> warning instead.
/// </summary>
/// <remarks>
/// Major metrics are, in this exact order: managed assembly count, aggregate type
/// count, aggregate method count, total managed bytes, then, for every assembly
/// identity in either baseline or candidate (ordered ordinal-ignore-case then
/// ordinal), that assembly's type count, and finally, for every such assembly
/// again in the same order, that assembly's method count.
/// </remarks>
internal static class ComparativeSanityValidator
{
    private const string CatastrophicSanityDeviationCode = "CatastrophicSanityDeviation";
    private const string ComparativeSanityDeviationCode = "ComparativeSanityDeviation";
    private const string BaselineZeroChangedCode = "BaselineZeroChanged";

    private const string WithinToleranceClassification = "WithinTolerance";
    private const string BaselineZeroChangedClassification = "BaselineZeroChanged";
    private const string ComparativeSanityDeviationClassification = "ComparativeSanityDeviation";
    private const string CatastrophicSanityDeviationClassification = "CatastrophicSanityDeviation";

    public static ComparativeSanityResult Validate(
        ExtractionStatistics baseline,
        ExtractionStatistics candidate,
        ResolvedValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);

        var comparisons = new List<ValidationMetricComparison>();
        var issues = new List<ValidationIssue>();

        comparisons.Add(Compare(
            "ManagedAssemblyCount", null, baseline.ManagedAssemblyCount, candidate.ManagedAssemblyCount,
            policy, issues));
        comparisons.Add(Compare(
            "TypeDefinitionCount", null, baseline.TypeDefinitionCount, candidate.TypeDefinitionCount,
            policy, issues));
        comparisons.Add(Compare(
            "MethodDefinitionCount", null, baseline.MethodDefinitionCount, candidate.MethodDefinitionCount,
            policy, issues));
        comparisons.Add(Compare(
            "TotalManagedBytes", null, baseline.TotalManagedBytes, candidate.TotalManagedBytes,
            policy, issues));

        var baselineAssemblies = ToLookup(baseline.Assemblies);
        var candidateAssemblies = ToLookup(candidate.Assemblies);
        var assemblyNames = baselineAssemblies.Keys
            .Concat(candidateAssemblies.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var name in assemblyNames)
        {
            var baselineTypeCount = baselineAssemblies.TryGetValue(name, out var baselineAssembly)
                ? baselineAssembly.TypeDefinitionCount
                : 0;
            var candidateTypeCount = candidateAssemblies.TryGetValue(name, out var candidateAssembly)
                ? candidateAssembly.TypeDefinitionCount
                : 0;
            comparisons.Add(Compare(
                "TypeDefinitionCount", name, baselineTypeCount, candidateTypeCount, policy, issues));
        }

        foreach (var name in assemblyNames)
        {
            var baselineMethodCount = baselineAssemblies.TryGetValue(name, out var baselineAssembly)
                ? baselineAssembly.MethodDefinitionCount
                : 0;
            var candidateMethodCount = candidateAssemblies.TryGetValue(name, out var candidateAssembly)
                ? candidateAssembly.MethodDefinitionCount
                : 0;
            comparisons.Add(Compare(
                "MethodDefinitionCount", name, baselineMethodCount, candidateMethodCount, policy, issues));
        }

        return new ComparativeSanityResult(comparisons, issues);
    }

    private static ValidationMetricComparison Compare(
        string metric,
        string? assemblyName,
        long baselineValue,
        long candidateValue,
        ResolvedValidationPolicy policy,
        List<ValidationIssue> issues)
    {
        if (baselineValue == 0)
        {
            if (candidateValue == 0)
            {
                return new ValidationMetricComparison(
                    metric, assemblyName, baselineValue, candidateValue, RelativeChange: null,
                    WithinToleranceClassification);
            }

            issues.Add(new ValidationIssue(
                ValidationIssueSeverity.Warning,
                BaselineZeroChangedCode,
                Describe(metric, assemblyName) +
                    $" had a zero baseline and changed to {candidateValue}.",
                ArtifactRelativePath: null,
                PreferenceBlocking: false));
            return new ValidationMetricComparison(
                metric, assemblyName, baselineValue, candidateValue, RelativeChange: null,
                BaselineZeroChangedClassification);
        }

        var relativeChange = Math.Abs(candidateValue - baselineValue) / (double)baselineValue;
        var isDecrease = candidateValue < baselineValue;

        if (isDecrease && relativeChange > policy.Policy.CatastrophicDecreaseRelativeChange)
        {
            issues.Add(new ValidationIssue(
                ValidationIssueSeverity.Error,
                CatastrophicSanityDeviationCode,
                Describe(metric, assemblyName) +
                    $" decreased by {relativeChange:P1} relative to baseline " +
                    $"({baselineValue} -> {candidateValue}), exceeding the catastrophic " +
                    $"decrease threshold of {policy.Policy.CatastrophicDecreaseRelativeChange:P1}.",
                ArtifactRelativePath: null,
                PreferenceBlocking: true));
            return new ValidationMetricComparison(
                metric, assemblyName, baselineValue, candidateValue, relativeChange,
                CatastrophicSanityDeviationClassification);
        }

        if (relativeChange > policy.Policy.ComparativeWarningRelativeChange)
        {
            issues.Add(new ValidationIssue(
                ValidationIssueSeverity.Warning,
                ComparativeSanityDeviationCode,
                Describe(metric, assemblyName) +
                    $" changed by {relativeChange:P1} relative to baseline " +
                    $"({baselineValue} -> {candidateValue}), exceeding the comparative " +
                    $"warning threshold of {policy.Policy.ComparativeWarningRelativeChange:P1}.",
                ArtifactRelativePath: null,
                PreferenceBlocking: false));
            return new ValidationMetricComparison(
                metric, assemblyName, baselineValue, candidateValue, relativeChange,
                ComparativeSanityDeviationClassification);
        }

        return new ValidationMetricComparison(
            metric, assemblyName, baselineValue, candidateValue, relativeChange,
            WithinToleranceClassification);
    }

    private static string Describe(string metric, string? assemblyName) =>
        assemblyName is null
            ? $"'{metric}'"
            : $"'{metric}' for assembly '{assemblyName}'";

    private static Dictionary<string, AssemblyIdentityStatistics> ToLookup(
        IReadOnlyList<AssemblyIdentityStatistics> assemblies)
    {
        var lookup = new Dictionary<string, AssemblyIdentityStatistics>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            lookup[assembly.AssemblyName] = assembly;
        }

        return lookup;
    }
}
