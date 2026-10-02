using System.Text.Json;
using S1Atlas.Application.Envelope;
using Xunit;

namespace S1Atlas.Web.Tests;

// Pins the exact wire bytes of the JSON options shared by MCP and the web
// API: converters, camelCase names, null omission, and escaping. Parsing
// tests cannot see escaping-only drift, so this golden does.
public sealed class ToolJsonGoldenTests
{
    [Fact]
    public void SharedOptionsPinExactWireBytes()
    {
        var envelope = ToolEnvelope<object>.NotFound(
            new BuildContext(
                RequestedBuildId: null,
                ResolvedBuildId: "build-1",
                ExtractionId: null,
                IndexId: "index-1",
                Codebase: "ScheduleI",
                Channel: "Installed",
                IntegrityVerified: true),
            new ToolError("SymbolNotFound", "Bad <tag> & \"quoted\" 'apostrophe' héllo \u2192"),
            new ProvenanceEntry(ProvenanceClassification.Fact, "index-search", "build-1", null, "index-1"));

        var json = JsonSerializer.Serialize(envelope, ToolJsonOptions.Create());

        Assert.Equal(
            "{\"status\":\"not_found\"," +
            "\"build\":{\"resolvedBuildId\":\"build-1\",\"indexId\":\"index-1\",\"codebase\":\"ScheduleI\",\"channel\":\"Installed\",\"integrityVerified\":true}," +
            "\"candidates\":[]," +
            "\"provenance\":[{\"classification\":\"FACT\",\"source\":\"index-search\",\"buildId\":\"build-1\",\"indexId\":\"index-1\"}]," +
            "\"error\":{\"code\":\"SymbolNotFound\",\"message\":\"Bad \\u003Ctag\\u003E \\u0026 \\u0022quoted\\u0022 \\u0027apostrophe\\u0027 h\\u00E9llo \\u2192\"}," +
            "\"suggestions\":[]}",
            json);
    }

    [Fact]
    public void AmbiguousEnvelopeReportsSuggestionsAndTotal()
    {
        var envelope = ToolEnvelope<object>.Ambiguous(
            build: null,
            new object[] { "shown" },
            12,
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", null, null, null));

        var json = JsonSerializer.Serialize(envelope, ToolJsonOptions.Create());

        Assert.Equal(
            "{\"status\":\"ambiguous\"," +
            "\"candidates\":[\"shown\"]," +
            "\"provenance\":[{\"classification\":\"DERIVED\",\"source\":\"installed-index\"}]," +
            "\"totalCandidateCount\":12," +
            "\"suggestions\":[]}",
            json);
    }
}
