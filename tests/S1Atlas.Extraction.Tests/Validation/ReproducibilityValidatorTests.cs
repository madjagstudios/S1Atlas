using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Validation;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class ReproducibilityValidatorTests
{
    [Fact]
    public void Validate_NoSameRecipeExtractions_ReturnsNewOutputWithNoIssues()
    {
        var result = ReproducibilityValidator.Validate("digest-a", []);

        Assert.Equal(ReproducibilityDisposition.NewOutput, result.Disposition);
        Assert.Null(result.ExistingExtraction);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_SameDigestAsExistingExtraction_ReturnsExistingOutputWithNoIssues()
    {
        var existing = Extraction("digest-a");

        var result = ReproducibilityValidator.Validate("digest-a", [existing]);

        Assert.Equal(ReproducibilityDisposition.ExistingOutput, result.Disposition);
        Assert.Same(existing, result.ExistingExtraction);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_DifferentDigestThanExistingExtraction_ReturnsNewOutputWithBlockingWarning()
    {
        var existing = Extraction("digest-a");

        var result = ReproducibilityValidator.Validate("digest-b", [existing]);

        Assert.Equal(ReproducibilityDisposition.NewOutput, result.Disposition);
        Assert.Null(result.ExistingExtraction);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SameRecipeDifferentOutput", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Warning, issue.Severity);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_MultipleRowsShareIdenticalRecipeAndDigest_ReturnsIntegrityError()
    {
        var first = Extraction("digest-a", extractionId: new string('1', 64));
        var second = Extraction("digest-a", extractionId: new string('2', 64));

        var result = ReproducibilityValidator.Validate("digest-a", [first, second]);

        Assert.Equal(ReproducibilityDisposition.NewOutput, result.Disposition);
        Assert.Null(result.ExistingExtraction);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("ReproducibilityIntegrityViolation", issue.Code);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.True(issue.PreferenceBlocking);
    }

    [Fact]
    public void Validate_DigestComparisonIsOrdinal_DoesNotTreatDifferentCaseAsEqual()
    {
        var existing = Extraction("DIGEST-A");

        var result = ReproducibilityValidator.Validate("digest-a", [existing]);

        Assert.Equal(ReproducibilityDisposition.NewOutput, result.Disposition);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SameRecipeDifferentOutput", issue.Code);
    }

    private static ValidatedExtraction Extraction(string artifactManifestDigest, string? extractionId = null) => new(
        ExtractionId: extractionId ?? new string('e', 64),
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
        RootPath: "C:/atlas/builds/build-a/extractions/existing",
        CreatedAtUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        TrustLevel: ToolTrustLevel.ManagedPinned,
        InitialValidationOutcome: ValidationOutcome.Valid,
        Statistics: new ExtractionStatistics(1, 1, 1, 1, 1, 0, 0, 0, 100, 100, []));
}
