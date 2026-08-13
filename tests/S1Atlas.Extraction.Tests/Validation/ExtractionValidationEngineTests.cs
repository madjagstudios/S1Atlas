using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
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
        var request = Request(candidateInspection: candidateInspection, artifactBuild: null);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Invalid, report.Outcome);
        Assert.False(report.OutputContainmentPassed);
        Assert.False(report.PreferenceEligible);
        Assert.Equal(string.Empty, report.ArtifactManifestDigest);
        Assert.Equal([containmentIssue], report.Issues);
        Assert.Equal(0, report.Statistics.ArtifactCount);
        Assert.Null(result.DeduplicationTarget);
        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_ContainmentPassedNoIssues_ReturnsValidReportEligibleForPreference()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

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
        var request = Request(artifactBuild: emptyBuild);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Invalid, report.Outcome);
        Assert.False(report.PreferenceEligible);
        Assert.Contains(report.Issues, issue => issue.Code == "NoArtifactsProduced");
        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_InputIntegrityFailed_ReportsValidOutcomeButNotPreferenceEligible()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild, inputIntegrityPassed: false);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.False(report.InputIntegrityPassed);
        Assert.False(report.PreferenceEligible);
        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_ProcessIntegrityFailed_ReportsValidOutcomeButNotPreferenceEligible()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild, processIntegrityPassed: false);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.False(report.ProcessIntegrityPassed);
        Assert.False(report.PreferenceEligible);
        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_ContainmentPassedWithoutArtifactBuild_ThrowsArgumentException()
    {
        var request = Request(artifactBuild: null);

        Assert.Throws<ArgumentException>(() => ExtractionValidationEngine.Evaluate(request));
    }

    [Fact]
    public void Evaluate_PopulatesReportProvenanceFromAttemptAndPolicy()
    {
        var attempt = Attempt();
        var policy = Policy();
        var artifactBuild = ValidArtifactBuild();
        var request = Request(
            attempt: attempt,
            subjectKind: ValidationSubjectKind.ValidatedExtraction,
            subjectExtractionId: new string('9', 64),
            artifactBuild: artifactBuild,
            policy: policy);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

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

    // --- Task 5: comparative sanity wiring ---

    [Fact]
    public void Evaluate_BaselineProvidedWithinTolerance_ReturnsComparisonsAndNoComparativeIssues()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(
            artifactBuild: artifactBuild,
            baselineStatistics: artifactBuild.Statistics,
            baselineExtractionId: new string('b', 64));

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Valid, report.Outcome);
        Assert.True(report.PreferenceEligible);
        Assert.Equal(new string('b', 64), report.BaselineExtractionId);
        Assert.NotEmpty(report.Comparisons);
        Assert.DoesNotContain(
            report.Issues, issue => issue.Code is "ComparativeSanityDeviation" or "CatastrophicSanityDeviation");
    }

    [Fact]
    public void Evaluate_BaselineCatastrophicDecrease_ReturnsInvalidWithBlockingIssueAndNoAutomaticPreference()
    {
        var candidate = ValidArtifactBuild();
        var baselineStatistics = candidate.Statistics with { ManagedAssemblyCount = 10 };
        var request = Request(
            artifactBuild: candidate,
            baselineStatistics: baselineStatistics,
            baselineExtractionId: new string('b', 64));

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.Invalid, report.Outcome);
        Assert.False(report.PreferenceEligible);
        Assert.Contains(report.Issues, issue =>
            issue.Code == "CatastrophicSanityDeviation" &&
            issue.Severity == ValidationIssueSeverity.Error &&
            issue.PreferenceBlocking);
        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_NoBaseline_ProducesNoComparisonsAndNullBaselineExtractionId()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild, baselineExtractionId: new string('b', 64));

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Empty(report.Comparisons);
        Assert.Null(report.BaselineExtractionId);
    }

    // --- Task 5: reproducibility wiring ---

    [Fact]
    public void Evaluate_SameRecipeSameDigest_ReturnsDeduplicationTargetAndNoNewOutputIssue()
    {
        var artifactBuild = ValidArtifactBuild();
        var existing = ValidatedExtractionFixture(
            extractionId: new string('e', 64), artifactManifestDigest: artifactBuild.ManifestDigest);
        var request = Request(artifactBuild: artifactBuild, sameRecipeExtractions: [existing]);

        var result = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ValidationOutcome.Valid, result.Report.Outcome);
        Assert.True(result.Report.PreferenceEligible);
        Assert.Same(existing, result.DeduplicationTarget);
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Code == "SameRecipeDifferentOutput");
    }

    [Fact]
    public void Evaluate_SameRecipeDifferentDigest_ReturnsBlockingWarningAndNoDeduplicationTarget()
    {
        var artifactBuild = ValidArtifactBuild();
        var existing = ValidatedExtractionFixture(
            extractionId: new string('e', 64), artifactManifestDigest: new string('f', 64));
        var request = Request(artifactBuild: artifactBuild, sameRecipeExtractions: [existing]);

        var result = ExtractionValidationEngine.Evaluate(request);
        var report = result.Report;

        Assert.Equal(ValidationOutcome.ValidWithWarnings, report.Outcome);
        Assert.False(report.PreferenceEligible);
        Assert.Contains(report.Issues, issue =>
            issue.Code == "SameRecipeDifferentOutput" && issue.PreferenceBlocking);
        Assert.Null(result.DeduplicationTarget);
        Assert.Null(result.AutomaticPreferenceReason);
    }

    // --- Task 5: automatic preference wiring ---

    [Fact]
    public void Evaluate_ManagedPinnedNoCurrentPreference_ReturnsManagedAutomatic()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild, toolTrustLevel: ToolTrustLevel.ManagedPinned);

        var result = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ExtractionPreferenceReason.ManagedAutomatic, result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_CustomOverride_NeverReturnsAutomaticPreference()
    {
        var artifactBuild = ValidArtifactBuild();
        var request = Request(artifactBuild: artifactBuild, toolTrustLevel: ToolTrustLevel.CustomOverride);

        var result = ExtractionValidationEngine.Evaluate(request);

        Assert.Null(result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_ManagedPinnedReplacingAutomaticFromDifferentToolInstance_ReturnsReplacementAfterToolUpgrade()
    {
        var artifactBuild = ValidArtifactBuild();
        var currentPreferred = new PreferredExtraction(
            "build-a", new string('c', 64), DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            ExtractionPreferenceReason.ManagedAutomatic);
        var request = Request(
            artifactBuild: artifactBuild,
            toolTrustLevel: ToolTrustLevel.ManagedPinned,
            currentPreferredExtraction: currentPreferred,
            currentPreferredToolInstanceId: "tool-0");

        var result = ExtractionValidationEngine.Evaluate(request);

        Assert.Equal(ExtractionPreferenceReason.ReplacementAfterToolUpgrade, result.AutomaticPreferenceReason);
    }

    [Fact]
    public void Evaluate_CurrentPreferenceIsManualPromotion_NeverReturnsAutomaticPreference()
    {
        var artifactBuild = ValidArtifactBuild();
        var currentPreferred = new PreferredExtraction(
            "build-a", new string('c', 64), DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            ExtractionPreferenceReason.ManualPromotion);
        var request = Request(
            artifactBuild: artifactBuild,
            toolTrustLevel: ToolTrustLevel.ManagedPinned,
            currentPreferredExtraction: currentPreferred,
            currentPreferredToolInstanceId: "tool-0");

        var result = ExtractionValidationEngine.Evaluate(request);

        Assert.Null(result.AutomaticPreferenceReason);
    }

    private static ExtractionValidationRequest Request(
        ExtractionAttempt? attempt = null,
        ValidationSubjectKind subjectKind = ValidationSubjectKind.CandidateOutput,
        string? subjectExtractionId = null,
        CandidateInspectionResult? candidateInspection = null,
        ArtifactBuildResult? artifactBuild = null,
        bool inputIntegrityPassed = true,
        bool processIntegrityPassed = true,
        ResolvedValidationPolicy? policy = null,
        ExtractionStatistics? baselineStatistics = null,
        string? baselineExtractionId = null,
        IReadOnlyList<ValidatedExtraction>? sameRecipeExtractions = null,
        ToolTrustLevel toolTrustLevel = ToolTrustLevel.ManagedPinned,
        PreferredExtraction? currentPreferredExtraction = null,
        string? currentPreferredToolInstanceId = null) =>
        new(
            attempt ?? Attempt(),
            subjectKind,
            subjectExtractionId,
            candidateInspection ?? PassedInspection(),
            artifactBuild,
            inputIntegrityPassed,
            processIntegrityPassed,
            policy ?? Policy(),
            baselineStatistics,
            baselineExtractionId,
            sameRecipeExtractions ?? [],
            toolTrustLevel,
            currentPreferredExtraction,
            currentPreferredToolInstanceId,
            ValidatedAtUtc);

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

    private static ValidatedExtraction ValidatedExtractionFixture(
        string extractionId, string artifactManifestDigest) => new(
        ExtractionId: extractionId,
        RecipeId: new string('2', 64),
        BuildId: "build-a",
        ToolInstanceId: "tool-1",
        SourceAttemptId: new string('1', 32),
        ProfileId: "profile",
        ProfileVersion: 1,
        ProfileDigest: new string('3', 64),
        AdapterVersion: 1,
        ExtractionSchemaVersion: 1,
        ArtifactManifestDigest: artifactManifestDigest,
        RootPath: "C:/atlas/builds/build-a/extractions/" + extractionId,
        CreatedAtUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        TrustLevel: ToolTrustLevel.ManagedPinned,
        InitialValidationOutcome: ValidationOutcome.Valid,
        Statistics: ValidArtifactBuild().Statistics);
}
