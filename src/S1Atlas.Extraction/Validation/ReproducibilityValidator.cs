using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// Whether a candidate's validated output is a brand-new production identity or
/// reproduces an already-validated extraction's exact bytes for the same recipe.
/// </summary>
internal enum ReproducibilityDisposition
{
    NewOutput,
    ExistingOutput
}

/// <summary>
/// The outcome of <see cref="ReproducibilityValidator.Validate"/>.
/// <see cref="ExistingExtraction"/> is set only when
/// <see cref="Disposition"/> is <see cref="ReproducibilityDisposition.ExistingOutput"/>,
/// naming the already-validated extraction a promoter should link the attempt to
/// (and whose duplicate candidate bytes it should discard after verification)
/// instead of producing a second copy of identical content.
/// </summary>
internal sealed record ReproducibilityResult(
    ReproducibilityDisposition Disposition,
    ValidatedExtraction? ExistingExtraction,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Compares a candidate's artifact manifest digest against every already-validated
/// extraction that shares the candidate's recipe ID (<paramref name="sameRecipeExtractions"/>,
/// which the caller has already filtered to that one recipe):
/// <list type="bullet">
/// <item>No same-recipe row at all -&gt; <see cref="ReproducibilityDisposition.NewOutput"/>,
/// no issues.</item>
/// <item>Exactly one same-recipe row with the identical digest -&gt;
/// <see cref="ReproducibilityDisposition.ExistingOutput"/>, no issues: the candidate
/// reproduces that extraction's bytes exactly.</item>
/// <item>One or more same-recipe rows, none matching the candidate's digest -&gt;
/// <see cref="ReproducibilityDisposition.NewOutput"/> plus a preference-blocking
/// <c>SameRecipeDifferentOutput</c> warning: this is a distinct extraction from an
/// equivalent recipe, so it never automatically replaces a current preference.</item>
/// <item>More than one same-recipe row shares the identical digest -&gt; a data
/// integrity defect (the unique recipe+digest constraint should make this
/// impossible), reported as a preference-blocking
/// <c>ReproducibilityIntegrityViolation</c> error.</item>
/// </list>
/// </summary>
internal static class ReproducibilityValidator
{
    private const string SameRecipeDifferentOutputCode = "SameRecipeDifferentOutput";
    private const string ReproducibilityIntegrityViolationCode = "ReproducibilityIntegrityViolation";

    public static ReproducibilityResult Validate(
        string artifactManifestDigest,
        IReadOnlyList<ValidatedExtraction> sameRecipeExtractions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactManifestDigest);
        ArgumentNullException.ThrowIfNull(sameRecipeExtractions);

        if (sameRecipeExtractions.Count == 0)
        {
            return new ReproducibilityResult(ReproducibilityDisposition.NewOutput, null, []);
        }

        var matching = sameRecipeExtractions
            .Where(extraction => string.Equals(
                extraction.ArtifactManifestDigest, artifactManifestDigest, StringComparison.Ordinal))
            .ToArray();

        if (matching.Length > 1)
        {
            return new ReproducibilityResult(
                ReproducibilityDisposition.NewOutput,
                null,
                [
                    new ValidationIssue(
                        ValidationIssueSeverity.Error,
                        ReproducibilityIntegrityViolationCode,
                        "More than one validated extraction shares this recipe and this " +
                            "candidate's exact artifact manifest digest.",
                        ArtifactRelativePath: null,
                        PreferenceBlocking: true)
                ]);
        }

        if (matching.Length == 1)
        {
            return new ReproducibilityResult(
                ReproducibilityDisposition.ExistingOutput, matching[0], []);
        }

        return new ReproducibilityResult(
            ReproducibilityDisposition.NewOutput,
            null,
            [
                new ValidationIssue(
                    ValidationIssueSeverity.Warning,
                    SameRecipeDifferentOutputCode,
                    "A previously validated extraction exists for this recipe with different " +
                        "output bytes; this candidate is a distinct extraction.",
                    ArtifactRelativePath: null,
                    PreferenceBlocking: true)
            ]);
    }
}
