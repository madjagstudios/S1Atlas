using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Promotion;
using Xunit;

namespace S1Atlas.Extraction.Tests.Promotion;

public sealed class ExtractionPreferencePolicyTests
{
    private static readonly DateTimeOffset SelectedAtUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void Decide_ManagedPinnedValidNoCurrentPreference_ReturnsManagedAutomatic()
    {
        var request = Request();

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Equal(ExtractionPreferenceReason.ManagedAutomatic, reason);
    }

    [Fact]
    public void Decide_ManagedPinnedValidReplacingAutomaticFromDifferentToolInstance_ReturnsReplacementAfterToolUpgrade()
    {
        var current = Preferred(ExtractionPreferenceReason.ManagedAutomatic);
        var request = Request(
            currentPreferred: current, currentPreferredToolInstanceId: "tool-old", toolInstanceId: "tool-new");

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Equal(ExtractionPreferenceReason.ReplacementAfterToolUpgrade, reason);
    }

    [Fact]
    public void Decide_ManagedPinnedValidReplacingReplacementFromDifferentToolInstance_ReturnsReplacementAfterToolUpgrade()
    {
        var current = Preferred(ExtractionPreferenceReason.ReplacementAfterToolUpgrade);
        var request = Request(
            currentPreferred: current, currentPreferredToolInstanceId: "tool-old", toolInstanceId: "tool-new");

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Equal(ExtractionPreferenceReason.ReplacementAfterToolUpgrade, reason);
    }

    [Fact]
    public void Decide_SameToolInstanceAsCurrentAutomaticPreference_ReturnsNoNewPreference()
    {
        var current = Preferred(ExtractionPreferenceReason.ManagedAutomatic, extractionId: new string('c', 64));
        var request = Request(
            candidateExtractionId: new string('d', 64),
            currentPreferred: current,
            currentPreferredToolInstanceId: "tool-1",
            toolInstanceId: "tool-1");

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    [Fact]
    public void Decide_CustomOverride_ReturnsNoAutomaticPreferenceEvenWhenValid()
    {
        var request = Request(toolTrustLevel: ToolTrustLevel.CustomOverride);

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    [Fact]
    public void Decide_OutcomeInvalid_ReturnsNoAutomaticPreference()
    {
        var request = Request(outcome: ValidationOutcome.Invalid);

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    [Fact]
    public void Decide_PreferenceBlockingIssuePresent_ReturnsNoAutomaticPreference()
    {
        var request = Request(preferenceEligible: false);

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    [Fact]
    public void Decide_CurrentPreferenceIsManualPromotion_IsNeverAutomaticallyOverwritten()
    {
        var current = Preferred(ExtractionPreferenceReason.ManualPromotion);
        var request = Request(
            currentPreferred: current, currentPreferredToolInstanceId: "tool-old", toolInstanceId: "tool-new");

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    [Fact]
    public void Decide_CandidateAlreadyThePreferredExtraction_ReturnsNoNewPreferenceEvent()
    {
        var current = Preferred(ExtractionPreferenceReason.ManagedAutomatic, extractionId: new string('c', 64));
        var request = Request(
            candidateExtractionId: new string('c', 64),
            currentPreferred: current,
            currentPreferredToolInstanceId: "tool-old",
            toolInstanceId: "tool-new");

        var reason = ExtractionPreferencePolicy.Decide(request);

        Assert.Null(reason);
    }

    private static ExtractionPreferenceDecisionRequest Request(
        ToolTrustLevel toolTrustLevel = ToolTrustLevel.ManagedPinned,
        string? toolInstanceId = "tool-1",
        string? candidateExtractionId = null,
        ValidationOutcome outcome = ValidationOutcome.Valid,
        bool preferenceEligible = true,
        PreferredExtraction? currentPreferred = null,
        string? currentPreferredToolInstanceId = null) => new(
        toolTrustLevel,
        toolInstanceId,
        candidateExtractionId,
        outcome,
        preferenceEligible,
        currentPreferred,
        currentPreferredToolInstanceId);

    private static PreferredExtraction Preferred(
        ExtractionPreferenceReason reason, string? extractionId = null) => new(
        "build-a", extractionId ?? new string('c', 64), SelectedAtUtc, reason);
}
