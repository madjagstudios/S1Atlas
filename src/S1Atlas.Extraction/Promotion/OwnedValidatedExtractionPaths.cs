using S1Atlas.Extraction.Attempts;

namespace S1Atlas.Extraction.Promotion;

/// <summary>
/// Owned, safety-checked filesystem locations for Phase 4 validation and
/// promotion. Reuses <see cref="OwnedAttemptPaths"/>' safety patterns (contained
/// path resolution, existing-ancestor reparse-point rejection) rather than
/// duplicating a more permissive check.
/// </summary>
/// <remarks>
/// <see cref="ForAttempt"/> exposes the attempt-scoped candidate path, the
/// attempt-level strict validation.json path (a sibling of the attempt's
/// "logs" directory, matching the convention
/// <c>SqliteAtlasRepository.ValidatedExtractions</c> derives from
/// <c>StandardOutputPath</c>), and the promotion staging locations. Promotion
/// staging deliberately reuses the exact same "&lt;build&gt;/extractions/.staging/
/// &lt;attempt-id&gt;" location Phase 3 already owns and safety-checks for that
/// attempt: it is transient and empty again once the attempt reaches
/// <c>ProcessCompleted</c>, so Phase 4 promotion can safely restage the same
/// owned attempt directory before atomically moving validated output into its
/// final extraction root. <see cref="ForExtraction"/> exposes the final,
/// content-addressed extraction root and the shared build-level quarantine root.
/// </remarks>
internal sealed record OwnedValidatedExtractionPaths
{
    private static readonly char[] UnsafeSegmentCharacters =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '<', '>', '"', '|', '?', '*'];

    private OwnedValidatedExtractionPaths(
        string dataRoot,
        string buildId,
        string? attemptId,
        string? extractionId,
        string? attemptCandidatePath,
        string? attemptValidationReportPath,
        string? promotionStagingRoot,
        string? promotionJournalPath,
        string? stagedReconstructedRoot,
        string? stagedLogsRoot,
        string? finalExtractionRoot,
        string quarantineRoot)
    {
        DataRoot = dataRoot;
        BuildId = buildId;
        AttemptId = attemptId;
        ExtractionId = extractionId;
        AttemptCandidatePath = attemptCandidatePath;
        AttemptValidationReportPath = attemptValidationReportPath;
        PromotionStagingRoot = promotionStagingRoot;
        PromotionJournalPath = promotionJournalPath;
        StagedReconstructedRoot = stagedReconstructedRoot;
        StagedLogsRoot = stagedLogsRoot;
        FinalExtractionRoot = finalExtractionRoot;
        QuarantineRoot = quarantineRoot;
    }

    public string DataRoot { get; }
    public string BuildId { get; }
    public string? AttemptId { get; }
    public string? ExtractionId { get; }

    /// <summary>The attempt's owned candidate output root: "&lt;attempt-root&gt;/candidate-output".</summary>
    public string? AttemptCandidatePath { get; }

    /// <summary>The attempt's strict validation.json: "&lt;attempt-root&gt;/validation.json".</summary>
    public string? AttemptValidationReportPath { get; }

    /// <summary>The promotion staging root: "builds/&lt;build&gt;/extractions/.staging/&lt;attempt-id&gt;".</summary>
    public string? PromotionStagingRoot { get; }

    /// <summary>
    /// The promotion journal, a sibling of <see cref="PromotionStagingRoot"/>:
    /// "builds/&lt;build&gt;/extractions/.staging/&lt;attempt-id&gt;.promotion.json".
    /// </summary>
    public string? PromotionJournalPath { get; }

    /// <summary>The staged reconstructed-artifact root under promotion staging.</summary>
    public string? StagedReconstructedRoot { get; }

    /// <summary>The staged logs root under promotion staging.</summary>
    public string? StagedLogsRoot { get; }

    /// <summary>The final, immutable extraction root: "builds/&lt;build&gt;/extractions/&lt;extraction-id&gt;".</summary>
    public string? FinalExtractionRoot { get; }

    /// <summary>The shared quarantine root: "builds/&lt;build&gt;/extractions/quarantine".</summary>
    public string QuarantineRoot { get; }

    public static OwnedValidatedExtractionPaths ForAttempt(
        string dataRoot,
        string buildId,
        string attemptId)
    {
        var owned = OwnedAttemptPaths.Create(dataRoot, buildId, attemptId);
        var quarantineRoot = ResolveContained(
            owned.DataRoot, "builds", buildId, "extractions", "quarantine");
        OwnedAttemptPaths.EnsureSafeExistingPath(owned.DataRoot, quarantineRoot);

        var promotionStagingRoot = owned.StagingRoot;
        var promotionJournalPath = promotionStagingRoot + ".promotion.json";

        return new OwnedValidatedExtractionPaths(
            dataRoot: owned.DataRoot,
            buildId: buildId,
            attemptId: attemptId,
            extractionId: null,
            attemptCandidatePath: owned.CandidateOutputRoot,
            attemptValidationReportPath: Path.Combine(owned.AttemptRoot, "validation.json"),
            promotionStagingRoot: promotionStagingRoot,
            promotionJournalPath: promotionJournalPath,
            stagedReconstructedRoot: Path.Combine(promotionStagingRoot, "reconstructed"),
            stagedLogsRoot: Path.Combine(promotionStagingRoot, "logs"),
            finalExtractionRoot: null,
            quarantineRoot: quarantineRoot);
    }

    public static OwnedValidatedExtractionPaths ForExtraction(
        string dataRoot,
        string buildId,
        string extractionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        RequireSafeSegment(buildId, "build ID");
        if (!IsLowerHex64(extractionId))
        {
            throw new ArgumentException(
                "The extraction ID must be a 64-character lower-case SHA-256 value.",
                nameof(extractionId));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var finalExtractionRoot = ResolveContained(
            root, "builds", buildId, "extractions", extractionId);
        var quarantineRoot = ResolveContained(
            root, "builds", buildId, "extractions", "quarantine");
        OwnedAttemptPaths.EnsureSafeExistingPath(root, finalExtractionRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(root, quarantineRoot);

        return new OwnedValidatedExtractionPaths(
            dataRoot: root,
            buildId: buildId,
            attemptId: null,
            extractionId: extractionId,
            attemptCandidatePath: null,
            attemptValidationReportPath: null,
            promotionStagingRoot: null,
            promotionJournalPath: null,
            stagedReconstructedRoot: null,
            stagedLogsRoot: null,
            finalExtractionRoot: finalExtractionRoot,
            quarantineRoot: quarantineRoot);
    }

    private static string ResolveContained(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!OwnedAttemptPaths.IsSameOrDescendant(root, candidate) ||
            string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The validated extraction path escapes its Atlas data root.");
        }

        return candidate;
    }

    private static void RequireSafeSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(UnsafeSegmentCharacters) >= 0 ||
            value.Any(char.IsControl) || Path.GetFileName(value) != value ||
            value.EndsWith(' ') || value.EndsWith('.'))
        {
            throw new ArgumentException(
                $"The {description} is not a safe path segment.",
                nameof(value));
        }
    }

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
