using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Promotion;

/// <summary>
/// Everything <see cref="ExtractionPreferencePolicy.Decide"/> needs to decide
/// whether a candidate should become a build's new automatically preferred
/// extraction.
/// </summary>
/// <param name="ToolInstanceId">
/// The candidate's tool instance ID, used only to detect a tool upgrade against
/// <see cref="CurrentPreferredToolInstanceId"/>. May be <see langword="null"/> when
/// unknown, in which case a tool-upgrade replacement is never decided.
/// </param>
/// <param name="CandidateExtractionId">
/// The extraction ID the candidate resolves to (an already-validated extraction's
/// ID when reproducibility found identical prior output; otherwise
/// <see langword="null"/> when the candidate has not yet been assigned one). Used
/// only to recognize "this extraction is already the current preference."
/// </param>
/// <param name="PreferenceEligible">
/// The report's own preference eligibility: non-<see cref="ValidationOutcome.Invalid"/>,
/// input/process integrity passed, and no preference-blocking issue.
/// </param>
/// <param name="CurrentPreferredToolInstanceId">
/// The tool instance ID of <see cref="CurrentPreferred"/>'s extraction, when known.
/// </param>
internal sealed record ExtractionPreferenceDecisionRequest(
    ToolTrustLevel ToolTrustLevel,
    string? ToolInstanceId,
    string? CandidateExtractionId,
    ValidationOutcome Outcome,
    bool PreferenceEligible,
    PreferredExtraction? CurrentPreferred,
    string? CurrentPreferredToolInstanceId);

/// <summary>
/// Decides whether a validated candidate automatically becomes a build's new
/// preferred extraction. Automatic selection is deliberately narrow:
/// <list type="bullet">
/// <item>A <see cref="ToolTrustLevel.CustomOverride"/> candidate is never
/// automatically preferred, even when it validates cleanly; it may validate and
/// remain in extraction history, but only a manual promotion can select it.</item>
/// <item>A candidate that is not preference-eligible (invalid, or carrying a
/// preference-blocking issue such as <c>CatastrophicSanityDeviation</c> or
/// <c>SameRecipeDifferentOutput</c>) is never automatically preferred.</item>
/// <item>A <see cref="ToolTrustLevel.ManagedPinned"/>, preference-eligible
/// candidate becomes preferred automatically
/// (<see cref="ExtractionPreferenceReason.ManagedAutomatic"/>) when the build has
/// no current preference.</item>
/// <item>The same candidate replaces a current, automatically-selected preference
/// from a <em>different</em> tool instance
/// (<see cref="ExtractionPreferenceReason.ReplacementAfterToolUpgrade"/>) — this is
/// the one case where an existing preference is overwritten automatically.</item>
/// <item>A current <see cref="ExtractionPreferenceReason.ManualPromotion"/> is
/// never automatically overwritten by anything.</item>
/// <item>When the candidate already <em>is</em> the current preference, no new
/// preference event is decided.</item>
/// </list>
/// </summary>
internal static class ExtractionPreferencePolicy
{
    public static ExtractionPreferenceReason? Decide(ExtractionPreferenceDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ToolTrustLevel != ToolTrustLevel.ManagedPinned)
        {
            return null;
        }

        if (request.Outcome == ValidationOutcome.Invalid || !request.PreferenceEligible)
        {
            return null;
        }

        var current = request.CurrentPreferred;
        if (current is null)
        {
            return ExtractionPreferenceReason.ManagedAutomatic;
        }

        if (request.CandidateExtractionId is not null &&
            string.Equals(current.ExtractionId, request.CandidateExtractionId, StringComparison.Ordinal))
        {
            return null;
        }

        if (current.SelectionReason == ExtractionPreferenceReason.ManualPromotion)
        {
            return null;
        }

        if (request.ToolInstanceId is not null &&
            request.CurrentPreferredToolInstanceId is not null &&
            !string.Equals(
                request.CurrentPreferredToolInstanceId, request.ToolInstanceId, StringComparison.Ordinal))
        {
            return ExtractionPreferenceReason.ReplacementAfterToolUpgrade;
        }

        return null;
    }
}
