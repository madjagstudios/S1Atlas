using S1Atlas.Core.Extraction;

namespace S1Atlas.Indexing.Authority;

public sealed record PreferredVerifiedExtraction(
    string BuildId,
    PreferredExtraction Preference,
    ValidatedExtraction Extraction);
