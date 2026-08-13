using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Validation;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class ExtractionValidationEngineTests
{
    private static readonly DateTimeOffset ValidatedAtUtc = DateTimeOffset.Parse("2026-08-12T12:00:00Z");

    [Fact]
    public void Evaluate_ContainmentFailed_ReturnsInvalidReportCarryingContainmentIssues()
    {
        var containmentIssue = new ValidationIssue(
            ValidationIssueSeverity.Error,
            "OutputOutsideStaging",
            "Candidate output escapes its owned candidate root.",
            null,
            PreferenceBlocking: true);
        var candidateInspection = new CandidateInspectionResult(
            Inventory: null, ContainmentPassed: false, Issues: [containmentIssue]);
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            candidateInspection,
            ArtifactBuild: null,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Invalid, report.Outcome);
        Assert.False(report.OutputContainmentPassed);
        Assert.False(report.PreferenceEligible);
        Assert.Equal(string.Empty, report.ArtifactManifestDigest);
        Assert.Equal([containmentIssue], report.Issues);
        Assert.Equal(0, report.Statistics.ArtifactCount);
    }

    [Fact]
    public void Evaluate_ContainmentPassedNoIssues_ReturnsValidReportEligibleForPreference()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            PassedInspection(),
            artifactBuild,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.True(report.OutputContainmentPassed);
        Assert.True(report.PreferenceEligible);
        Assert.Equal(artifactBuild.ManifestDigest, report.ArtifactManifestDigest);
        Assert.Same(artifactBuild.Statistics, report.Statistics);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Evaluate_AbsoluteSanityHardIssue_ReturnsInvalidAndNotPreferenceEligible()
    {
        var emptyBuild = EmptyArtifactBuild();
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            PassedInspection(),
            emptyBuild,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Invalid, report.Outcome);
        Assert.False(report.PreferenceEligible);
        Assert.Contains(report.Issues, issue => issue.Code == "NoArtifactsProduced");
    }

    [Fact]
    public void Evaluate_InputIntegrityFailed_ReportsValidOutcomeButNotPreferenceEligible()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            PassedInspection(),
            artifactBuild,
            InputIntegrityPassed: false,
            ProcessIntegrityPassed: true,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.False(report.InputIntegrityPassed);
        Assert.False(report.PreferenceEligible);
    }

    [Fact]
    public void Evaluate_ProcessIntegrityFailed_ReportsValidOutcomeButNotPreferenceEligible()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            PassedInspection(),
            artifactBuild,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: false,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.False(report.ProcessIntegrityPassed);
        Assert.False(report.PreferenceEligible);
    }

    [Fact]
    public void Evaluate_ContainmentPassedWithoutArtifactBuild_ThrowsArgumentException()
    {
        var request = new ExtractionValidationRequest(
            Attempt(),
            ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            PassedInspection(),
            ArtifactBuild: null,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            Policy(),
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        Assert.Throws<ArgumentException>(() => ExtractionValidationEngine.Evaluate(request));
    }

    [Fact]
    public void Evaluate_PopulatesReportProvenanceFromAttemptAndPolicy()
    {
        var attempt = Attempt();
        var policy = Policy();
        var artifactBuild = ValidArtifactBuild();
        var request = new ExtractionValidationRequest(
            attempt,
            ValidationSubjectKind.ValidatedExtraction,
            SubjectExtractionId: new string('9', 64),
            PassedInspection(),
            artifactBuild,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            policy,
            BaselineStatistics: null,
            BaselineExtractionId: null,
            SameRecipeExtractions: [],
            ValidatedAtUtc);

        var report = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(attempt.AttemptId, report.AttemptId);
        Assert.Equal(attempt.BuildId, report.BuildId);
        Assert.Equal(attempt.RecipeId, report.RecipeId);
        Assert.Equal(ValidationSubjectKind.ValidatedExtraction, report.SubjectKind);
        Assert.Equal(new string('9', 64), report.SubjectExtractionId);
        Assert.Equal(policy.Policy.PolicyId, report.PolicyId);
        Assert.Equal(policy.Policy.PolicyVersion, report.PolicyVersion);
        Assert.Equal(policy.PolicyDigest, report.PolicyDigest);
        Assert.Equal(ValidatedAtUtc, report.ValidatedAtUtc);
        Assert.Null(report.BaselineExtractionId);
        Assert.Empty(report.Comparisons);
    }

    private static ExtractionAttempt Attempt() => new(
        AttemptId: new string('1', 32),
        RecipeId: new string('2', 64),
        BuildId: "build-a",
        ToolInstanceId: "tool-1",
        ProfileId: "profile",
        ProfileVersion: 1,
        ProfileDigest: new string('3', 64),
        ValidationPolicyId: "test-policy",
        ValidationPolicyVersion: 1,
        ValidationPolicyDigest: new string('4', 64),
        AdapterVersion: 1,
        ExtractionSchemaVersion: 1,
        InputSource: ExtractionInputSource.Live,
        InputSnapshotId: null,
        Status: ExtractionAttemptStatus.ProcessCompleted,
        CreatedAtUtc: DateTimeOffset.Parse("2026-08-12T11:00:00Z"),
        StartedAtUtc: DateTimeOffset.Parse("2026-08-12T11:01:00Z"),
        CompletedAtUtc: DateTimeOffset.Parse("2026-08-12T11:05:00Z"),
        PreInputManifestDigest: new string('5', 64),
        PostInputManifestDigest: new string('6', 64),
        WorkingPath: "C:/atlas/work",
        StandardOutputPath: "C:/atlas/logs/stdout.log",
        StandardErrorPath: "C:/atlas/logs/stderr.log",
        StandardOutputTruncated: false,
        StandardErrorTruncated: false,
        StandardOutputDiscardedBytes: 0,
        StandardErrorDiscardedBytes: 0,
        ProcessId: 1234,
        ProcessExitCode: 0,
        FailureStage: null,
        FailureCode: null,
        FailureMessage: null,
        KeepFailedArtifacts: true,
        DiscardedFileCount: 0,
        DiscardedByteCount: 0,
        CandidateOutputPath: "C:/atlas/candidate-output",
        ResultExtractionId: null);

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

    private static CandidateInspectionResult PassedInspection() => new(
        Inventory: new CandidateInventory("candidate-root", [], 0),
        ContainmentPassed: true,
        Issues: []);

    private static ArtifactBuildResult ValidArtifactBuild()
    {
        var entry = new ArtifactManifestEntry(
            "reconstructed/Assembly-CSharp.dll",
            ArtifactKind.ManagedAssembly,
            200,
            new string('a', 64),
            "Assembly-CSharp",
            "Assembly-CSharp.dll",
            TypeDefinitionCount: 2,
            MethodDefinitionCount: 7,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 1);
        var manifest = new ArtifactManifest(1, [entry]);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 2,
            MethodDefinitionCount: 7,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 1,
            TotalOutputBytes: 200,
            TotalManagedBytes: 200,
            Assemblies: [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 200, 2, 7, 2, 1, 1)]);
        return new ArtifactBuildResult(manifest, "manifest-digest", statistics, []);
    }

    private static ArtifactBuildResult EmptyArtifactBuild()
    {
        var manifest = new ArtifactManifest(1, []);
        var statistics = new ExtractionStatistics(
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
        return new ArtifactBuildResult(manifest, "empty-digest", statistics, []);
    }
}
