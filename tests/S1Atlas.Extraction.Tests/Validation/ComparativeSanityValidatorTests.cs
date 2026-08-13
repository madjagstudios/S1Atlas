using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Validation;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class ComparativeSanityValidatorTests
{
    [Fact]
    public void Validate_ManagedAssemblyCountDecreasesMoreThan80Percent_ReturnsCatastrophicError()
    {
        var baseline = Statistics(managedAssemblyCount: 100);
        var candidate = Statistics(managedAssemblyCount: 15); // 85% decrease

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("CatastrophicSanityDeviation", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.True(issue.PreferenceBlocking);
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "ManagedAssemblyCount");
        Assert.Equal(0.85, comparison.RelativeChange);
        Assert.Equal("CatastrophicSanityDeviation", comparison.Classification);
    }

    [Fact]
    public void Validate_ManagedAssemblyCountDecreasesExactly80Percent_IsNotCatastrophicButIsWarning()
    {
        var baseline = Statistics(managedAssemblyCount: 100);
        var candidate = Statistics(managedAssemblyCount: 20); // exactly 80% decrease

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("ComparativeSanityDeviation", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Warning, issue.Severity);
        Assert.False(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_ManagedAssemblyCountChangesExactly25Percent_ProducesNoIssue()
    {
        var baseline = Statistics(managedAssemblyCount: 100);
        var candidate = Statistics(managedAssemblyCount: 75); // exactly 25% decrease

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        Assert.Empty(result.Issues);
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "ManagedAssemblyCount");
        Assert.Equal(0.25, comparison.RelativeChange);
        Assert.Equal("WithinTolerance", comparison.Classification);
    }

    [Fact]
    public void Validate_ManagedAssemblyCountChangesMoreThan25PercentByIncrease_ReturnsWarning()
    {
        var baseline = Statistics(managedAssemblyCount: 100);
        var candidate = Statistics(managedAssemblyCount: 200); // 100% increase

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("ComparativeSanityDeviation", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Warning, issue.Severity);
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "ManagedAssemblyCount");
        Assert.Equal(1.0, comparison.RelativeChange);
    }

    [Fact]
    public void Validate_LargeIncreaseWellBeyondThreshold_ReturnsWarningNotError()
    {
        var baseline = Statistics(totalManagedBytes: 1_000);
        var candidate = Statistics(totalManagedBytes: 50_000); // 4900% increase

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("ComparativeSanityDeviation", issue.Code);
    }

    [Fact]
    public void Validate_ZeroBaselineNonZeroCandidate_ReturnsBaselineZeroChangedWarningWithoutDividingByZero()
    {
        var baseline = Statistics(totalManagedBytes: 0);
        var candidate = Statistics(totalManagedBytes: 5);

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("BaselineZeroChanged", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Warning, issue.Severity);
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "TotalManagedBytes");
        Assert.Null(comparison.RelativeChange);
        Assert.Equal("BaselineZeroChanged", comparison.Classification);
    }

    [Fact]
    public void Validate_ZeroBaselineZeroCandidate_ProducesNoIssue()
    {
        var baseline = Statistics(totalManagedBytes: 0);
        var candidate = Statistics(totalManagedBytes: 0);

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        Assert.Empty(result.Issues);
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "TotalManagedBytes");
        Assert.Null(comparison.RelativeChange);
        Assert.Equal("WithinTolerance", comparison.Classification);
    }

    [Fact]
    public void Validate_AggregateMetricsPrecedePerAssemblyMetricsInComparisonsOrder()
    {
        var baseline = StatisticsWithAssemblies(("Assembly-CSharp", 2, 7));
        var candidate = StatisticsWithAssemblies(("Assembly-CSharp", 2, 7));

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        Assert.Equal(
            [
                "ManagedAssemblyCount", "TypeDefinitionCount", "MethodDefinitionCount", "TotalManagedBytes",
                "TypeDefinitionCount", "MethodDefinitionCount"
            ],
            result.Comparisons.Select(c => c.Metric));
        Assert.Null(result.Comparisons[0].AssemblyName);
        Assert.Null(result.Comparisons[1].AssemblyName);
        Assert.Null(result.Comparisons[2].AssemblyName);
        Assert.Null(result.Comparisons[3].AssemblyName);
        Assert.Equal("Assembly-CSharp", result.Comparisons[4].AssemblyName);
        Assert.Equal("Assembly-CSharp", result.Comparisons[5].AssemblyName);
    }

    [Fact]
    public void Validate_MultipleAssemblies_OrdersNamesOrdinalIgnoreCaseThenOrdinal()
    {
        var baseline = StatisticsWithAssemblies(("Zeta", 1, 1), ("alpha", 1, 1), ("Beta", 1, 1));
        var candidate = StatisticsWithAssemblies(("Zeta", 1, 1), ("alpha", 1, 1), ("Beta", 1, 1));

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        var perAssemblyTypeComparisons = result.Comparisons
            .Where(c => c.Metric == "TypeDefinitionCount" && c.AssemblyName != null)
            .Select(c => c.AssemblyName!)
            .ToArray();
        Assert.Equal(["alpha", "Beta", "Zeta"], perAssemblyTypeComparisons);
    }

    [Fact]
    public void Validate_PerAssemblyTypeCountCatastrophicDecrease_ReturnsErrorScopedToThatAssembly()
    {
        var baseline = StatisticsWithAssemblies(("Assembly-CSharp", 100, 100));
        var candidate = StatisticsWithAssemblies(("Assembly-CSharp", 10, 100)); // 90% type decrease

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        // Both the aggregate TypeDefinitionCount and this assembly's own
        // TypeDefinitionCount decrease by the same 90%, since it is the only
        // assembly, so both a global and an assembly-scoped issue are expected.
        Assert.All(result.Issues, issue => Assert.Equal("CatastrophicSanityDeviation", issue.Code));
        var comparison = Assert.Single(result.Comparisons, c => c.Metric == "TypeDefinitionCount" && c.AssemblyName == "Assembly-CSharp");
        Assert.Equal("CatastrophicSanityDeviation", comparison.Classification);
    }

    [Fact]
    public void Validate_AssemblyMissingFromCandidate_TreatsMissingCountsAsZero()
    {
        var baseline = StatisticsWithAssemblies(("Assembly-CSharp", 10, 10));
        var candidate = Statistics(); // no assemblies at all

        var result = ComparativeSanityValidator.Validate(baseline, candidate, Policy());

        Assert.Contains(result.Comparisons, c =>
            c.Metric == "TypeDefinitionCount" && c.AssemblyName == "Assembly-CSharp" &&
            c.BaselineValue == 10 && c.CandidateValue == 0);
    }

    private static ResolvedValidationPolicy Policy()
    {
        var policy = new ValidationPolicy(
            SchemaVersion: 1,
            PolicyId: "test-policy",
            PolicyVersion: 1,
            RequiredAssemblyIdentities: [],
            MinimumManagedAssemblyCount: 1,
            MinimumTypeDefinitionCount: 1,
            MinimumMethodDefinitionCount: 1,
            MinimumTotalManagedBytes: 1,
            ComparativeWarningRelativeChange: 0.25,
            CatastrophicDecreaseRelativeChange: 0.80);
        return new ResolvedValidationPolicy(policy, ValidationPolicyFingerprint.Create(policy));
    }

    private static ExtractionStatistics Statistics(
        int managedAssemblyCount = 1,
        long totalManagedBytes = 100) => new(
        ArtifactCount: managedAssemblyCount,
        LibraryCount: managedAssemblyCount,
        ManagedAssemblyCount: managedAssemblyCount,
        TypeDefinitionCount: 1,
        MethodDefinitionCount: 1,
        FieldDefinitionCount: 0,
        PropertyDefinitionCount: 0,
        EventDefinitionCount: 0,
        TotalOutputBytes: totalManagedBytes,
        TotalManagedBytes: totalManagedBytes,
        Assemblies: []);

    private static ExtractionStatistics StatisticsWithAssemblies(
        params (string Name, int TypeCount, int MethodCount)[] assemblies)
    {
        var assemblyStatistics = assemblies
            .Select(a => new AssemblyIdentityStatistics(a.Name, 1, 100, a.TypeCount, a.MethodCount, 0, 0, 0))
            .ToArray();
        return new ExtractionStatistics(
            ArtifactCount: assemblyStatistics.Length,
            LibraryCount: assemblyStatistics.Length,
            ManagedAssemblyCount: assemblyStatistics.Length,
            TypeDefinitionCount: assemblyStatistics.Sum(a => a.TypeDefinitionCount),
            MethodDefinitionCount: assemblyStatistics.Sum(a => a.MethodDefinitionCount),
            FieldDefinitionCount: 0,
            PropertyDefinitionCount: 0,
            EventDefinitionCount: 0,
            TotalOutputBytes: assemblyStatistics.Sum(a => a.ManagedBytes),
            TotalManagedBytes: assemblyStatistics.Sum(a => a.ManagedBytes),
            Assemblies: assemblyStatistics);
    }
}
