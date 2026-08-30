namespace S1Atlas.Core.Storage;

public enum NativeRecoveryStatus
{
    Recovered,
    NoBody,
    AmbiguousMapping,
    InputChanged,
    Failed,
    Unsupported
}

public sealed record NativeRecoveryRequest(
    string BuildId,
    string IndexId,
    string GameAssemblySha256,
    IReadOnlyList<string> SymbolIds,
    int MaxTraversalEdges);

public sealed record NativeRecoveryExecutionContext(
    string CurrentBuildId,
    string CurrentIndexId,
    string CurrentGameAssemblySha256,
    string ToolName,
    string ToolVersion,
    string ToolSha256);

public sealed record NativeEvidenceEdge(
    string EdgeId,
    string SourceMethodPointer,
    string? TargetMethodPointer,
    string? TargetText,
    string Kind,
    string Evidence,
    bool IsComplete);

public sealed record NativeRecoveryRecord(
    string RecoveryId,
    NativeRecoveryRequest Request,
    string ToolName,
    string ToolVersion,
    string ToolSha256,
    NativeRecoveryStatus Status,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeEvidenceEdge> Edges,
    IReadOnlyList<string> FieldAccesses,
    bool IsComplete,
    string OutputSha256,
    DateTimeOffset CreatedAtUtc,
    string? FailureMessage);
