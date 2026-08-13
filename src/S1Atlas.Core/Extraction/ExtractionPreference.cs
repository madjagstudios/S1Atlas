namespace S1Atlas.Core.Extraction;

public enum ExtractionPreferenceReason
{
    ManagedAutomatic,
    ManualPromotion,
    ReplacementAfterToolUpgrade,
    PolicyInvalidated,
    IntegrityInvalidated
}

public sealed record PreferredExtraction(
    string BuildId,
    string ExtractionId,
    DateTimeOffset SelectedAtUtc,
    ExtractionPreferenceReason SelectionReason);

public sealed record ExtractionPreferenceEvent(
    string EventId,
    string BuildId,
    string? PreviousExtractionId,
    string? NewExtractionId,
    DateTimeOffset SelectedAtUtc,
    ExtractionPreferenceReason SelectionReason);
