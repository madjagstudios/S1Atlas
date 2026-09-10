namespace S1Atlas.Cli.Output;

internal sealed record NativeRecoveryEdgeOutput(
    string SourceMethodPointer,
    string? TargetMethodPointer,
    string? TargetText,
    string Kind,
    string Evidence,
    bool IsComplete);

internal sealed record NativeRecoveryCliOutput(
    string RecoveryId,
    string Status,
    bool IsComplete,
    string ToolName,
    string ToolVersion,
    string ToolSha256,
    string OutputSha256,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeRecoveryEdgeOutput> Edges,
    IReadOnlyList<string> FieldAccesses,
    string? FailureMessage);
