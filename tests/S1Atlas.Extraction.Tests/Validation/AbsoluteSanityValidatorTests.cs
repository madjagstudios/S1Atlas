using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Validation;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class AbsoluteSanityValidatorTests
{
    [Fact]
    public void Validate_NoArtifacts_ReturnsNoArtifactsProducedOnly()
    {
        var manifest = new ArtifactManifest(1, []);
        var statistics = EmptyStatistics();
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: []);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("NoArtifactsProduced", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_EmptyArtifactAlongsideValidAssembly_ReportsEmptyArtifactForThatPathOnly()
    {
        var emptyEntry = Entry("reconstructed/empty.bin", ArtifactKind.Other, size: 0);
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [emptyEntry, managedEntry]);
        var statistics = StatisticsFor([managedEntry], [emptyEntry, managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: [], minAssemblies: 1, minTypes: 1, minMethods: 1, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("EmptyArtifact", issue.Code);
        Assert.Equal("reconstructed/empty.bin", issue.ArtifactRelativePath);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_ArtifactsPresentButNoManagedAssembly_ReturnsNoManagedAssembliesProduced()
    {
        var otherEntry = Entry("reconstructed/kernel32.dll", ArtifactKind.NativeLibrary, size: 10);
        var manifest = new ArtifactManifest(1, [otherEntry]);
        var statistics = StatisticsFor([], [otherEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: []);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("NoManagedAssembliesProduced", issue.Code);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_RequiredIdentityAbsent_ReturnsRequiredAssemblyMissing()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Other.dll", "Other", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: ["Assembly-CSharp"], minAssemblies: 1, minTypes: 1, minMethods: 1, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("RequiredAssemblyMissing", issue.Code);
        Assert.Contains("Assembly-CSharp", issue.Message);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_ForwardsInvalidManagedAssemblyIssueFromArtifactBuild()
    {
        var invalidEntry = Entry("reconstructed/broken.dll", ArtifactKind.Other, size: 4);
        var manifest = new ArtifactManifest(1, [invalidEntry]);
        var statistics = StatisticsFor([], [invalidEntry]);
        var builderIssue = new ValidationIssue(
            ValidationIssueSeverity.Error,
            "InvalidManagedAssembly",
            "'reconstructed/broken.dll' is not a valid managed assembly.",
            "reconstructed/broken.dll",
            PreferenceBlocking: true);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, [builderIssue]);
        var policy = Policy(requiredIdentities: []);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        Assert.Contains(issues, issue => issue.Code == "InvalidManagedAssembly" && issue.PreferenceBlocking);
        // NoManagedAssembliesProduced also fires since the only artifact is invalid.
        Assert.Equal(
            ["NoManagedAssembliesProduced", "InvalidManagedAssembly"],
            issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Validate_ForwardsDuplicateAssemblyIdentityIssueFromArtifactBuild()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var builderIssue = new ValidationIssue(
            ValidationIssueSeverity.Information,
            "DuplicateAssemblyIdentity",
            "Assembly identity 'Assembly-CSharp' appears 2 times with identical content.",
            null,
            PreferenceBlocking: false);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, [builderIssue]);
        var policy = Policy(requiredIdentities: [], minAssemblies: 1, minTypes: 1, minMethods: 1, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("DuplicateAssemblyIdentity", issue.Code);
        Assert.False(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_ManagedAssemblyCountBelowPolicyMinimum_ReturnsInsufficientManagedAssemblyCount()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: [], minAssemblies: 2, minTypes: 1, minMethods: 1, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("InsufficientManagedAssemblyCount", issue.Code);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_TypeDefinitionCountBelowPolicyMinimum_ReturnsInsufficientTypeDefinitionCount()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: [], minAssemblies: 1, minTypes: 100, minMethods: 1, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("InsufficientTypeDefinitionCount", issue.Code);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_MethodDefinitionCountBelowPolicyMinimum_ReturnsInsufficientMethodDefinitionCount()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: [], minAssemblies: 1, minTypes: 1, minMethods: 100, minBytes: 1);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("InsufficientMethodDefinitionCount", issue.Code);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_TotalManagedBytesBelowPolicyMinimum_ReturnsInsufficientManagedBytes()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(requiredIdentities: [], minAssemblies: 1, minTypes: 1, minMethods: 1, minBytes: 1_048_576);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        var issue = Assert.Single(issues);
        Assert.Equal("InsufficientManagedBytes", issue.Code);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_AllFactsSatisfyPolicy_ReturnsNoIssues()
    {
        var managedEntry = ManagedEntry(
            "reconstructed/Assembly-CSharp.dll", "Assembly-CSharp", size: 200, types: 2, methods: 7);
        var manifest = new ArtifactManifest(1, [managedEntry]);
        var statistics = StatisticsFor([managedEntry], [managedEntry]);
        var build = new ArtifactBuildResult(manifest, "digest", statistics, []);
        var policy = Policy(
            requiredIdentities: ["Assembly-CSharp"], minAssemblies: 1, minTypes: 1, minMethods: 1, minBytes: 100);

        var issues = AbsoluteSanityValidator.Validate(build, policy);

        Assert.Empty(issues);
    }

    private static ExtractionStatistics EmptyStatistics() => new(
        ArtifactCount: 0,
        LibraryCount: 0,
        ManagedAssemblyCount: 0,
        TypeDefinitionCount: 0,
        MethodDefinitionCount: 0,
        FieldDefinitionCount: 0,
        PropertyDefinitionCount: 0,
        EventDefinitionCount: 0,
        TotalOutputBytes: 0,
        TotalManagedBytes: 0,
        Assemblies: []);

    private static ArtifactManifestEntry Entry(string relativePath, ArtifactKind kind, long size) => new(
        relativePath, kind, size, new string('a', 64), null, null, null, null, null, null, null);

    private static ArtifactManifestEntry ManagedEntry(
        string relativePath, string assemblyName, long size, int types, int methods) => new(
        relativePath,
        ArtifactKind.ManagedAssembly,
        size,
        new string('b', 64),
        assemblyName,
        assemblyName + ".dll",
        types,
        methods,
        FieldDefinitionCount: 1,
        PropertyDefinitionCount: 1,
        EventDefinitionCount: 1);

    private static ExtractionStatistics StatisticsFor(
        IReadOnlyList<ArtifactManifestEntry> managedEntries, IReadOnlyList<ArtifactManifestEntry> allEntries)
    {
        var assemblies = managedEntries
            .GroupBy(entry => entry.AssemblyName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AssemblyIdentityStatistics(
                group.Key,
                group.Count(),
                group.Sum(entry => entry.Size),
                group.Sum(entry => entry.TypeDefinitionCount ?? 0),
                group.Sum(entry => entry.MethodDefinitionCount ?? 0),
                group.Sum(entry => entry.FieldDefinitionCount ?? 0),
                group.Sum(entry => entry.PropertyDefinitionCount ?? 0),
                group.Sum(entry => entry.EventDefinitionCount ?? 0)))
            .ToArray();

        return new ExtractionStatistics(
            ArtifactCount: allEntries.Count,
            LibraryCount: allEntries.Count(entry =>
                entry.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
            ManagedAssemblyCount: managedEntries.Count,
            TypeDefinitionCount: assemblies.Sum(assembly => assembly.TypeDefinitionCount),
            MethodDefinitionCount: assemblies.Sum(assembly => assembly.MethodDefinitionCount),
            FieldDefinitionCount: assemblies.Sum(assembly => assembly.FieldDefinitionCount),
            PropertyDefinitionCount: assemblies.Sum(assembly => assembly.PropertyDefinitionCount),
            EventDefinitionCount: assemblies.Sum(assembly => assembly.EventDefinitionCount),
            TotalOutputBytes: allEntries.Sum(entry => entry.Size),
            TotalManagedBytes: managedEntries.Sum(entry => entry.Size),
            Assemblies: assemblies);
    }

    private static ResolvedValidationPolicy Policy(
        IReadOnlyList<string> requiredIdentities,
        int minAssemblies = 0,
        int minTypes = 0,
        int minMethods = 0,
        long minBytes = 0)
    {
        var policy = new ValidationPolicy(
            SchemaVersion: 1,
            PolicyId: "test-policy",
            PolicyVersion: 1,
            RequiredAssemblyIdentities: requiredIdentities,
            MinimumManagedAssemblyCount: minAssemblies,
            MinimumTypeDefinitionCount: minTypes,
            MinimumMethodDefinitionCount: minMethods,
            MinimumTotalManagedBytes: minBytes,
            ComparativeWarningRelativeChange: 0.25,
            CatastrophicDecreaseRelativeChange: 0.80);
        return new ResolvedValidationPolicy(policy, ValidationPolicyFingerprint.Create(policy));
    }
}
