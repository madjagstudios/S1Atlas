using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Application.Envelope;

[JsonConverter(typeof(ToolStatusJsonConverter))]
public enum ToolStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Unavailable,
    Invalid
}

[JsonConverter(typeof(ProvenanceClassificationJsonConverter))]
public enum ProvenanceClassification
{
    Fact,
    Derived,
    Interpretation
}

public sealed class ToolStatusJsonConverter : JsonConverter<ToolStatus>
{
    public override ToolStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "resolved" or "Resolved" => ToolStatus.Resolved,
            "not_found" or "NotFound" => ToolStatus.NotFound,
            "ambiguous" or "Ambiguous" => ToolStatus.Ambiguous,
            "unavailable" or "Unavailable" => ToolStatus.Unavailable,
            "invalid" or "Invalid" => ToolStatus.Invalid,
            _ => throw new JsonException("Unknown tool status.")
        };

    public override void Write(Utf8JsonWriter writer, ToolStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ToolStatus.Resolved => "resolved",
            ToolStatus.NotFound => "not_found",
            ToolStatus.Ambiguous => "ambiguous",
            ToolStatus.Unavailable => "unavailable",
            ToolStatus.Invalid => "invalid",
            _ => throw new JsonException("Unknown tool status.")
        });
}

public sealed class ProvenanceClassificationJsonConverter : JsonConverter<ProvenanceClassification>
{
    public override ProvenanceClassification Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "FACT" or "Fact" => ProvenanceClassification.Fact,
            "DERIVED" or "Derived" => ProvenanceClassification.Derived,
            "INTERPRETATION" or "Interpretation" => ProvenanceClassification.Interpretation,
            _ => throw new JsonException("Unknown provenance classification.")
        };

    public override void Write(Utf8JsonWriter writer, ProvenanceClassification value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ProvenanceClassification.Fact => "FACT",
            ProvenanceClassification.Derived => "DERIVED",
            ProvenanceClassification.Interpretation => "INTERPRETATION",
            _ => throw new JsonException("Unknown provenance classification.")
        });
}

public sealed record BuildContext(
    string? RequestedBuildId,
    string? ResolvedBuildId,
    string? ExtractionId,
    string? IndexId,
    string Codebase,
    string Channel,
    bool IntegrityVerified);

public sealed record ProvenanceEntry(
    ProvenanceClassification Classification,
    string Source,
    string? BuildId,
    string? ExtractionId,
    string? IndexId);

public sealed record ToolError(string Code, string Message, string? Hint = null);

// The wire shape for symbol candidates and suggestions: everything a
// follow-up call needs except the index identity, which the envelope build
// already carries.
public sealed record SlimSymbolCandidate(
    string SymbolId,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort,
    string? Origin = null,
    string? Collection = null,
    string? ReferenceModId = null,
    string? DisplayName = null,
    string? Version = null,
    string? License = null,
    string? RelativePath = null,
    string? Sha256 = null,
    string? ShortId = null)
{
    public static SlimSymbolCandidate From(SymbolQueryResult symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return new(
            symbol.SymbolId,
            symbol.Kind,
            symbol.QualifiedName,
            symbol.Signature,
            symbol.IsBestEffort,
            symbol.Origin,
            symbol.Collection,
            symbol.ReferenceModId,
            symbol.DisplayName,
            symbol.Version,
            symbol.License,
            symbol.RelativePath,
            symbol.Sha256,
            symbol.ShortId);
    }
}

public sealed record ToolEnvelope<T>(
    ToolStatus Status,
    BuildContext? Build,
    T? Data,
    IReadOnlyList<object> Candidates,
    IReadOnlyList<ProvenanceEntry> Provenance,
    ToolError? Error,
    IReadOnlyList<object>? Suggestions = null,
    int? TotalCandidateCount = null) where T : class
{
    public IReadOnlyList<object> Suggestions { get; init; } = Suggestions ?? [];

    public BuildContext? BuildA { get; init; }
    public BuildContext? BuildB { get; init; }

    public static ToolEnvelope<T> Resolved(
        BuildContext? build,
        T data,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Resolved,
            build,
            data,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, EnsureFactProvenance(build, provenance)),
            null);

    public static ToolEnvelope<T> NotFound(
        BuildContext? build,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.NotFound,
            build,
            null,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, provenance),
            null);

    public static ToolEnvelope<T> NotFound(
        BuildContext? build,
        ToolError? error = null,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.NotFound,
            build,
            null,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, provenance),
            error);

    public static ToolEnvelope<T> NotFound(
        BuildContext? build,
        ToolError? error,
        IReadOnlyList<object>? suggestions,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.NotFound,
            build,
            null,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, provenance),
            error,
            suggestions is null ? null : SlimCandidates(suggestions));

    public static ToolEnvelope<T> Ambiguous(
        BuildContext? build,
        IReadOnlyList<object> candidates,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Ambiguous,
            build,
            null,
            SlimCandidates(candidates),
            StripDuplicateBuildIds(build, provenance),
            null);

    public static ToolEnvelope<T> Ambiguous(
        BuildContext? build,
        IReadOnlyList<object> candidates,
        int? totalCandidateCount,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Ambiguous,
            build,
            null,
            SlimCandidates(candidates),
            StripDuplicateBuildIds(build, provenance),
            null,
            null,
            totalCandidateCount);

    public static ToolEnvelope<T> Unavailable(
        ToolError error,
        BuildContext? build = null,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Unavailable,
            build,
            null,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, provenance),
            error);

    public static ToolEnvelope<T> Invalid(
        ToolError error,
        BuildContext? build = null,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Invalid,
            build,
            null,
            Array.Empty<object>(),
            StripDuplicateBuildIds(build, provenance),
            error);

    public static IReadOnlyList<ProvenanceEntry> StripDuplicateBuildIds(
        BuildContext? build,
        IReadOnlyList<ProvenanceEntry> provenance)
    {
        if (build is null || provenance.Count == 0)
            return provenance;

        ProvenanceEntry[]? stripped = null;
        for (var i = 0; i < provenance.Count; i++)
        {
            var entry = provenance[i];
            var buildId = string.Equals(entry.BuildId, build.ResolvedBuildId, StringComparison.Ordinal) ? null : entry.BuildId;
            var extractionId = string.Equals(entry.ExtractionId, build.ExtractionId, StringComparison.Ordinal) ? null : entry.ExtractionId;
            var indexId = string.Equals(entry.IndexId, build.IndexId, StringComparison.Ordinal) ? null : entry.IndexId;
            if (!ReferenceEquals(buildId, entry.BuildId) ||
                !ReferenceEquals(extractionId, entry.ExtractionId) ||
                !ReferenceEquals(indexId, entry.IndexId))
            {
                stripped ??= provenance.ToArray();
                stripped[i] = entry with { BuildId = buildId, ExtractionId = extractionId, IndexId = indexId };
            }
        }

        return stripped ?? provenance;
    }

    private static IReadOnlyList<object> SlimCandidates(IReadOnlyList<object> candidates)
    {
        if (candidates.Count == 0)
            return candidates;

        object[]? slimmed = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] is SymbolQueryResult symbol)
            {
                slimmed ??= candidates.ToArray();
                slimmed[i] = SlimSymbolCandidate.From(symbol);
            }
        }

        return slimmed ?? candidates;
    }

    private static IReadOnlyList<ProvenanceEntry> EnsureFactProvenance(
        BuildContext? build,
        IReadOnlyList<ProvenanceEntry> provenance)
    {
        if (provenance.Count > 0 && provenance.Any(entry => entry.Classification == ProvenanceClassification.Fact))
        {
            return provenance;
        }

        if (build is null)
        {
            return provenance;
        }

        var fact = new ProvenanceEntry(
            ProvenanceClassification.Fact,
            "installed-build-authority",
            build.ResolvedBuildId ?? build.RequestedBuildId,
            build.ExtractionId,
            build.IndexId);

        if (provenance.Count == 0)
        {
            return new[] { fact };
        }

        var entries = new ProvenanceEntry[provenance.Count + 1];
        entries[0] = fact;
        for (var i = 0; i < provenance.Count; i++)
        {
            entries[i + 1] = provenance[i];
        }
        return entries;
    }
}
