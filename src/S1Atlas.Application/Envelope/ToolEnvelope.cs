using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record ToolError(string Code, string Message);

public sealed record ToolEnvelope<T>(
    ToolStatus Status,
    BuildContext? Build,
    T? Data,
    IReadOnlyList<object> Candidates,
    IReadOnlyList<ProvenanceEntry> Provenance,
    ToolError? Error) where T : class
{
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
            EnsureFactProvenance(build, provenance),
            null);

    public static ToolEnvelope<T> NotFound(
        BuildContext? build,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.NotFound,
            build,
            null,
            Array.Empty<object>(),
            provenance,
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
            provenance,
            error);

    public static ToolEnvelope<T> Ambiguous(
        BuildContext? build,
        IReadOnlyList<object> candidates,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Ambiguous,
            build,
            null,
            candidates,
            provenance,
            null);

    public static ToolEnvelope<T> Unavailable(
        ToolError error,
        BuildContext? build = null,
        params ProvenanceEntry[] provenance) =>
        new(
            ToolStatus.Unavailable,
            build,
            null,
            Array.Empty<object>(),
            provenance,
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
            provenance,
            error);

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
