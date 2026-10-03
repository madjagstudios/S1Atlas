using System.Text.Json;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// The lean envelope contract: empty candidates/suggestions are omitted,
// provenance IDs that duplicate the envelope build are stripped, symbol
// candidates slim to the fields a follow-up call needs, and call-site /
// field-reference results serialize flat without the doubled page object.
public sealed class McpEnvelopeSlimTests
{
    private static readonly BuildContext Build = new(
        RequestedBuildId: null,
        ResolvedBuildId: "build-1",
        ExtractionId: "extraction-1",
        IndexId: "index-1",
        Codebase: "ScheduleI",
        Channel: "Installed",
        IntegrityVerified: true);

    [Fact]
    public void ResolvedEnvelopeOmitsEmptyCandidatesAndSuggestions()
    {
        var envelope = ToolEnvelope<string>.Resolved(
            Build,
            "data",
            new ProvenanceEntry(ProvenanceClassification.Fact, "index-search", "build-1", "extraction-1", "index-1"));

        using var document = Serialize(envelope);

        Assert.False(document.RootElement.TryGetProperty("candidates", out _));
        Assert.False(document.RootElement.TryGetProperty("suggestions", out _));
    }

    [Fact]
    public void AmbiguousEnvelopeKeepsNonEmptyCandidates()
    {
        var envelope = ToolEnvelope<string>.Ambiguous(
            Build,
            [new SymbolQueryResult("index-1", "ScheduleI", "Installed", "symbol-a", "Method", "A.Run", "A", false)],
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", "build-1", "extraction-1", "index-1"));

        using var document = Serialize(envelope);

        Assert.Equal(1, document.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public void ProvenanceStripsIdsDuplicatingBuild()
    {
        var envelope = ToolEnvelope<string>.Resolved(
            Build,
            "data",
            new ProvenanceEntry(ProvenanceClassification.Fact, "index-search", "build-1", "extraction-1", "index-1"));

        using var document = Serialize(envelope);
        var entry = Assert.Single(document.RootElement.GetProperty("provenance").EnumerateArray());

        Assert.Equal("FACT", entry.GetProperty("classification").GetString());
        Assert.Equal("index-search", entry.GetProperty("source").GetString());
        Assert.False(entry.TryGetProperty("buildId", out _));
        Assert.False(entry.TryGetProperty("extractionId", out _));
        Assert.False(entry.TryGetProperty("indexId", out _));
    }

    [Fact]
    public void ProvenanceKeepsIdsDifferingFromBuild()
    {
        var envelope = ToolEnvelope<string>.Resolved(
            Build,
            "data",
            new ProvenanceEntry(ProvenanceClassification.Derived, "reference-collection", "build-9", "extraction-9", "index-9"));

        using var document = Serialize(envelope);
        var entries = document.RootElement.GetProperty("provenance").EnumerateArray().ToArray();
        var entry = Assert.Single(entries, item => item.GetProperty("source").GetString() == "reference-collection");

        Assert.Equal("build-9", entry.GetProperty("buildId").GetString());
        Assert.Equal("extraction-9", entry.GetProperty("extractionId").GetString());
        Assert.Equal("index-9", entry.GetProperty("indexId").GetString());
    }

    [Fact]
    public void SymbolCandidatesSlimToFollowUpFields()
    {
        var envelope = ToolEnvelope<string>.Ambiguous(
            Build,
            [new SymbolQueryResult("index-1", "ScheduleI", "Installed", "symbol-a", "Method", "A.Run", "A", false, Origin: "game")],
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", "build-1", "extraction-1", "index-1"));

        using var document = Serialize(envelope);
        var candidate = Assert.Single(document.RootElement.GetProperty("candidates").EnumerateArray());

        Assert.Equal("symbol-a", candidate.GetProperty("symbolId").GetString());
        Assert.Equal("A.Run", candidate.GetProperty("qualifiedName").GetString());
        Assert.False(candidate.TryGetProperty("indexId", out _));
        Assert.False(candidate.TryGetProperty("codebase", out _));
        Assert.False(candidate.TryGetProperty("channel", out _));
    }

    [Fact]
    public void NonSymbolCandidatesPassThroughUnchanged()
    {
        var envelope = ToolEnvelope<string>.Ambiguous(
            Build,
            ["shown"],
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", "build-1", "extraction-1", "index-1"));

        using var document = Serialize(envelope);

        Assert.Equal("shown", Assert.Single(document.RootElement.GetProperty("candidates").EnumerateArray()).GetString());
    }

    [Fact]
    public void CallSiteResultSerializesFlatWithoutPage()
    {
        var result = new CallSiteQueryResult(
            new RelationshipQueryPageResult(2, 1, [TestRelationship()]),
            TargetRelationshipQueryNotices.CallSites);

        using var document = Serialize(result);

        Assert.False(document.RootElement.TryGetProperty("page", out _));
        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("returnedCount").GetInt32());
        Assert.Single(document.RootElement.GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public void FieldReferenceResultSerializesFlatWithoutPage()
    {
        var result = new FieldReferenceQueryResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, null, []),
            new RelationshipQueryPageResult(2, 1, [TestRelationship()]));

        using var document = Serialize(result);

        Assert.False(document.RootElement.TryGetProperty("page", out _));
        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("returnedCount").GetInt32());
        Assert.Single(document.RootElement.GetProperty("relationships").EnumerateArray());
    }

    private static RelationshipQueryResult TestRelationship() =>
        new(
            "rel-1",
            "Calls",
            "evidence",
            "Incoming",
            new RelationshipEndpointQueryResult("source-1", "Source.One", "Source.One", null, true),
            new RelationshipEndpointQueryResult("target-1", "Target.One", "Target.One", null, true));

    private static JsonDocument Serialize<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, ToolJsonOptions.Create()));
}
