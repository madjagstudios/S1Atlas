using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Promotion;
using Xunit;

namespace S1Atlas.Extraction.Tests.Promotion;

// Task 3 exercises OwnedValidatedExtractionPaths directly rather than through
// ValidatedExtractionPromoterTests: the plan's own File Structure places
// ValidatedExtractionPromoter.cs and its promotion machinery in Task 6, so no
// promoter exists yet for path-focused promoter tests to drive. These
// path-focused cases give OwnedValidatedExtractionPaths the coverage the plan
// calls for while Task 6 remains unimplemented.
public sealed class OwnedValidatedExtractionPathsTests : IDisposable
{
    private const string AttemptId = "0123456789abcdef0123456789abcdef";
    private static readonly string ExtractionId = new('7', 64);

    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-validated-extraction-paths-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public void ForAttempt_ReturnsPathsDerivedFromOwnedAttemptPaths()
    {
        Directory.CreateDirectory(_dataRoot);
        var owned = OwnedAttemptPaths.Create(_dataRoot, "build-a", AttemptId);

        var paths = OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, "build-a", AttemptId);

        Assert.Equal(owned.CandidateOutputRoot, paths.AttemptCandidatePath);
        Assert.Equal(
            Path.Combine(owned.AttemptRoot, "validation.json"),
            paths.AttemptValidationReportPath);
        Assert.Equal(owned.StagingRoot, paths.PromotionStagingRoot);
        Assert.Equal(owned.StagingRoot + ".promotion.json", paths.PromotionJournalPath);
        Assert.Equal(
            Path.Combine(owned.StagingRoot, "reconstructed"), paths.StagedReconstructedRoot);
        Assert.Equal(Path.Combine(owned.StagingRoot, "logs"), paths.StagedLogsRoot);
        Assert.Equal(
            Path.Combine(_dataRoot, "builds", "build-a", "extractions", "quarantine"),
            paths.QuarantineRoot);
        Assert.Null(paths.FinalExtractionRoot);
        Assert.Null(paths.ExtractionId);
    }

    [Fact]
    public void ForAttempt_ValidationReportPathIsSiblingOfAttemptLogsDirectory()
    {
        Directory.CreateDirectory(_dataRoot);
        var owned = OwnedAttemptPaths.Create(_dataRoot, "build-a", AttemptId);

        var paths = OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, "build-a", AttemptId);

        // Task 2's SqliteAtlasRepository.ValidatedExtractions derives the strict
        // validation.json path by taking the parent of the attempt's "logs"
        // directory (StandardOutputPath's grandparent). This must agree exactly
        // so a later task's writes land where the stored report_path expects.
        var derivedFromLogs = Path.GetDirectoryName(
            Path.GetDirectoryName(Path.Combine(owned.FinalLogsRoot, "stdout.log")));
        Assert.Equal(
            Path.Combine(derivedFromLogs!, "validation.json"),
            paths.AttemptValidationReportPath);
    }

    [Fact]
    public void ForAttempt_PromotionJournalIsSiblingOfStagingRoot()
    {
        Directory.CreateDirectory(_dataRoot);

        var paths = OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, "build-a", AttemptId);

        Assert.Equal(
            Path.GetDirectoryName(paths.PromotionStagingRoot),
            Path.GetDirectoryName(paths.PromotionJournalPath));
        Assert.NotEqual(paths.PromotionStagingRoot, paths.PromotionJournalPath);
    }

    [Theory]
    [InlineData("../escape", AttemptId)]
    [InlineData("BUILD/child", AttemptId)]
    [InlineData("build-a", "not-a-guid")]
    public void ForAttempt_UnsafeSegments_AreRejected(string buildId, string attemptId)
    {
        Directory.CreateDirectory(_dataRoot);
        Assert.Throws<ArgumentException>(
            () => OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, buildId, attemptId));
    }

    [Fact]
    public void ForExtraction_ReturnsFinalRootAndQuarantineRoot()
    {
        Directory.CreateDirectory(_dataRoot);

        var paths = OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, "build-a", ExtractionId);

        Assert.Equal(
            Path.Combine(_dataRoot, "builds", "build-a", "extractions", ExtractionId),
            paths.FinalExtractionRoot);
        Assert.Equal(
            Path.Combine(_dataRoot, "builds", "build-a", "extractions", "quarantine"),
            paths.QuarantineRoot);
        Assert.Null(paths.AttemptCandidatePath);
        Assert.Null(paths.PromotionStagingRoot);
        Assert.Null(paths.AttemptId);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("BUILD/child")]
    public void ForExtraction_UnsafeBuildId_IsRejected(string buildId)
    {
        Directory.CreateDirectory(_dataRoot);
        Assert.Throws<ArgumentException>(
            () => OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, buildId, ExtractionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-lowercase-hex")]
    public void ForExtraction_InvalidExtractionId_IsRejected(string extractionId)
    {
        Directory.CreateDirectory(_dataRoot);
        Assert.Throws<ArgumentException>(
            () => OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, "build-a", extractionId));
    }

    [Fact]
    public void ForExtraction_UppercaseDigest_IsRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        Assert.Throws<ArgumentException>(
            () => OwnedValidatedExtractionPaths.ForExtraction(
                _dataRoot, "build-a", new string('A', 64)));
    }

    [Fact]
    public void ForExtraction_FileAsAncestor_IsRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "builds", "build-a"));
        File.WriteAllText(
            Path.Combine(_dataRoot, "builds", "build-a", "extractions"), "not-a-directory");

        Assert.Throws<InvalidOperationException>(
            () => OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, "build-a", ExtractionId));
    }

    [Fact]
    public void ForAttempt_QuarantineRootAndForExtraction_QuarantineRoot_AreTheSamePath()
    {
        Directory.CreateDirectory(_dataRoot);

        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, "build-a", AttemptId);
        var extractionPaths = OwnedValidatedExtractionPaths.ForExtraction(
            _dataRoot, "build-a", ExtractionId);

        Assert.Equal(attemptPaths.QuarantineRoot, extractionPaths.QuarantineRoot);
    }
}
