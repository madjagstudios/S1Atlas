using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diffing;

namespace S1Atlas.Cli.Output;

internal sealed record DiffOutput(
    DiffSnapshotOutput From,
    DiffSnapshotOutput To,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<DiffChangeOutput> Changes);

internal sealed record DiffSnapshotOutput(
    string IndexId,
    string SnapshotId,
    string Codebase,
    string Channel,
    string SourceIdentity,
    string FidelityBasis);

internal sealed record DiffChangeOutput(
    string ComparisonKey,
    string LineageKey,
    string? FromSymbolId,
    string? ToSymbolId,
    string? FromQualifiedName,
    string? ToQualifiedName,
    string? FromSignature,
    string? ToSignature,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<DiffEvidenceOutput> Evidence,
    IReadOnlyList<DiffRelationshipOutput> Relationships,
    string? FromBodyRecoveryStatus,
    string? ToBodyRecoveryStatus);

internal sealed record DiffEvidenceOutput(string Layer, string? From, string? To);

internal sealed record DiffRelationshipOutput(
    string Kind,
    string Change,
    DiffEndpointOutput Source,
    DiffEndpointOutput Target,
    string Evidence);

internal sealed record DiffEndpointOutput(
    string? SymbolId,
    string? ComparisonKey,
    string? QualifiedName,
    string? Signature,
    string? RawText,
    bool Resolved);

internal sealed record DiffFailureData(IReadOnlyList<DiffChangeOutput> Candidates);

internal static class DiffOutputMapper
{
    public static DiffOutput Map(IndexDiffResult result, IReadOnlyList<SymbolDiff> changes) =>
        new(
            Snapshot(result.FromIndexId, result.From, result.FromFidelityBasis),
            Snapshot(result.ToIndexId, result.To, result.ToFidelityBasis),
            changes.Count,
            changes.Count,
            changes.Select(Map).ToArray());

    private static DiffSnapshotOutput Snapshot(string indexId, CodeSnapshotRecord snapshot, string fidelityBasis) =>
        new(
            indexId,
            snapshot.SnapshotId,
            snapshot.Codebase.ToString(),
            snapshot.Channel.ToString(),
            snapshot.SourceIdentity,
            fidelityBasis);

    public static DiffChangeOutput Map(SymbolDiff change) =>
        new(
            change.ComparisonKey,
            change.LineageKey,
            change.FromSymbolId,
            change.ToSymbolId,
            change.FromQualifiedName,
            change.ToQualifiedName,
            change.FromSignature,
            change.ToSignature,
            change.Kinds.Select(kind => kind.ToString()).ToArray(),
            change.Evidence.Select(evidence => new DiffEvidenceOutput(evidence.Layer, evidence.From, evidence.To)).ToArray(),
            change.Relationships.Select(relationship => new DiffRelationshipOutput(
                relationship.Kind,
                relationship.Change.ToString(),
                Map(relationship.Source),
                Map(relationship.Target),
                relationship.Evidence)).ToArray(),
            change.FromBodyRecoveryStatus?.ToString(),
            change.ToBodyRecoveryStatus?.ToString());

    private static DiffEndpointOutput Map(DiffEndpoint endpoint) =>
        new(endpoint.SymbolId, endpoint.ComparisonKey, endpoint.QualifiedName, endpoint.Signature, endpoint.RawText, endpoint.Resolved);
}
