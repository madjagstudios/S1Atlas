using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diffing;
using Xunit;

namespace S1Atlas.Indexing.Tests.Diffing;

public sealed class SymbolDiffEngineTests
{
    [Fact]
    public void Cross_channel_matching_strips_channel_and_reports_unavailable_body()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("old", "S1Api:Installed:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.StubOrUnavailable)],
            [Fingerprint("old", "declaration", "same-declaration"), Fingerprint("old", "structural", "same-structure")]);
        var to = Snapshot(
            CodeChannel.Release,
            [Symbol("new", "S1Api:Release:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.Recovered)],
            [Fingerprint("new", "declaration", "same-declaration"), Fingerprint("new", "structural", "same-structure")]);

        var result = new SymbolDiffEngine().Compare(from, to);

        var change = Assert.Single(result.Changes);
        Assert.Equal("Method:Demo.Service::Run()", change.ComparisonKey);
        Assert.Contains(SymbolChangeKind.BodyUnavailable, change.Kinds);
        Assert.DoesNotContain(SymbolChangeKind.Unchanged, change.Kinds);
        Assert.Equal(CodeChannel.Installed, result.From.Channel);
        Assert.Equal(CodeChannel.Release, result.To.Channel);
    }

    [Fact]
    public void Same_lineage_with_different_signature_is_paired_and_classified()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("old", "ScheduleI:Installed:Method:Demo.Service::Run(System.Int32)", "Demo.Service.Run", "System.Void Demo.Service::Run(System.Int32)", BodyRecoveryStatus.Recovered)],
            [Fingerprint("old", "declaration", "old-declaration"), Fingerprint("old", "structural", "old-structure")]);
        var to = Snapshot(
            CodeChannel.Installed,
            [Symbol("new", "ScheduleI:Installed:Method:Demo.Service::Run(System.String)", "Demo.Service.Run", "System.Void Demo.Service::Run(System.String)", BodyRecoveryStatus.Recovered)],
            [Fingerprint("new", "declaration", "new-declaration"), Fingerprint("new", "structural", "new-structure")]);

        var change = Assert.Single(new SymbolDiffEngine().Compare(from, to).Changes);

        Assert.Contains(SymbolChangeKind.SignatureChanged, change.Kinds);
        Assert.Equal("Method:Demo.Service::Run", change.LineageKey);
        Assert.Equal("old", change.FromSymbolId);
        Assert.Equal("new", change.ToSymbolId);
    }

    [Fact]
    public void Relationship_deltas_preserve_kind_evidence_and_unresolved_targets()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("source-old", "ScheduleI:Installed:Method:Demo.Source::Run()", "Demo.Source.Run", "System.Void Demo.Source::Run()", BodyRecoveryStatus.Recovered)],
            [Fingerprint("source-old", "declaration", "same"), Fingerprint("source-old", "structural", "same"), Fingerprint("source-old", "method-body", "same")],
            [Relationship("removed", "source-old", null, "External.Api::Old()", "Calls", "unresolved")]);
        var to = Snapshot(
            CodeChannel.Installed,
            [Symbol("source-new", "ScheduleI:Installed:Method:Demo.Source::Run()", "Demo.Source.Run", "System.Void Demo.Source::Run()", BodyRecoveryStatus.Recovered)],
            [Fingerprint("source-new", "declaration", "same"), Fingerprint("source-new", "structural", "same"), Fingerprint("source-new", "method-body", "same")],
            [Relationship("added", "source-new", null, "External.Api::New()", "Calls", "unresolved")]);

        var change = Assert.Single(new SymbolDiffEngine().Compare(from, to).Changes);

        Assert.Contains(SymbolChangeKind.RelationshipsChanged, change.Kinds);
        Assert.Contains(change.Relationships, relationship => relationship.Change == RelationshipChangeKind.Removed && relationship.Target.RawText == "External.Api::Old()" && relationship.Evidence == "unresolved");
        Assert.Contains(change.Relationships, relationship => relationship.Change == RelationshipChangeKind.Added && relationship.Target.RawText == "External.Api::New()");
    }

    [Fact]
    public void Identical_recovered_snapshots_are_deterministic_and_unchanged()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("source", "ScheduleI:Installed:Method:Demo.Source::Run()", "Demo.Source.Run", "System.Void Demo.Source::Run()", BodyRecoveryStatus.Recovered)],
            [Fingerprint("source", "declaration", "same"), Fingerprint("source", "structural", "same"), Fingerprint("source", "method-body", "same"), Fingerprint("source", "source", "same")]);

        var first = new SymbolDiffEngine().Compare(from, from);
        var second = new SymbolDiffEngine().Compare(from, from);

        var change = Assert.Single(first.Changes);
        Assert.Equal([SymbolChangeKind.Unchanged], change.Kinds);
        Assert.Equal(first.Changes.Single().ComparisonKey, second.Changes.Single().ComparisonKey);
        Assert.Equal(first.Changes.Single().Kinds, second.Changes.Single().Kinds);
        Assert.Equal(first.Changes.Single().Evidence, second.Changes.Single().Evidence);
    }

    [Fact]
    public void Standalone_body_unavailability_is_classified_but_not_a_meaningful_delta()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("old", "S1Api:Installed:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.StubOrUnavailable)],
            [Fingerprint("old", "declaration", "same"), Fingerprint("old", "structural", "same")]);
        var to = Snapshot(
            CodeChannel.Installed,
            [Symbol("new", "S1Api:Installed:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.StubOrUnavailable)],
            [Fingerprint("new", "declaration", "same"), Fingerprint("new", "structural", "same")]);

        var change = Assert.Single(new SymbolDiffEngine().Compare(from, to).Changes);

        Assert.Contains(SymbolChangeKind.BodyUnavailable, change.Kinds);
        Assert.False(change.IsMeaningfulChange);
    }

    [Fact]
    public void Body_recovery_transition_is_a_meaningful_delta_even_without_body_change()
    {
        var from = Snapshot(
            CodeChannel.Installed,
            [Symbol("old", "S1Api:Installed:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.StubOrUnavailable)],
            [Fingerprint("old", "declaration", "same"), Fingerprint("old", "structural", "same")]);
        var to = Snapshot(
            CodeChannel.Installed,
            [Symbol("new", "S1Api:Installed:Method:Demo.Service::Run()", "Demo.Service.Run", "System.Void Demo.Service::Run()", BodyRecoveryStatus.Recovered)],
            [Fingerprint("new", "declaration", "same"), Fingerprint("new", "structural", "same")]);

        var change = Assert.Single(new SymbolDiffEngine().Compare(from, to).Changes);

        Assert.Contains(SymbolChangeKind.BodyUnavailable, change.Kinds);
        Assert.True(change.IsMeaningfulChange);
    }

    private static IndexSnapshotFacts Snapshot(
        CodeChannel channel,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyList<IndexFingerprintRecord> fingerprints,
        IReadOnlyList<IndexRelationshipRecord>? relationships = null) =>
        new(
            new CodeSnapshotRecord("snapshot-" + channel, CodebaseKind.S1Api, channel, "source-" + channel, "2026-08-14T00:00:00Z"),
            new IndexRunRecord("index-" + channel, "snapshot-" + channel, IndexRunStatus.Completed, "2026-08-14T00:00:00Z", "2026-08-14T00:01:00Z"),
            symbols,
            fingerprints,
            relationships ?? []);

    private static IndexSymbolRecord Symbol(string id, string canonicalKey, string qualifiedName, string signature, BodyRecoveryStatus? bodyRecoveryStatus) =>
        new(id, "snapshot", canonicalKey, canonicalKey.Split(':')[2], qualifiedName, signature, false, bodyRecoveryStatus);

    private static IndexFingerprintRecord Fingerprint(string symbolId, string kind, string value) =>
        new(symbolId, kind, value);

    private static IndexRelationshipRecord Relationship(string id, string source, string? targetId, string? targetText, string kind, string evidence) =>
        new(id, "snapshot", source, targetId, targetText, kind, evidence);
}
