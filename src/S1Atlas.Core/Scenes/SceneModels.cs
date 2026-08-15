namespace S1Atlas.Core.Scenes;

public enum SceneDocumentKind { Scene, Prefab }
public enum SceneSnapshotStatus { Running, Completed, Failed }
public enum SceneResolutionStatus { Resolved, UnresolvedText, Ambiguous, NotIndexed, Unavailable }

public sealed record SceneSnapshotRecord(
    string SceneSnapshotId,
    string BuildId,
    string ExtractionId,
    string InputSnapshotId,
    string CodeSnapshotId,
    string CodeIndexId,
    string ParserId,
    string ParserVersion,
    string ContainerManifestDigest,
    SceneSnapshotStatus Status,
    SceneRecoveryStatus RecoveryStatus,
    string StartedAtUtc,
    string? CompletedAtUtc = null,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string BuildId { get; init; } = SceneContract.RequireId(BuildId, nameof(BuildId));
    public string ExtractionId { get; init; } = SceneContract.RequireId(ExtractionId, nameof(ExtractionId));
    public string InputSnapshotId { get; init; } = SceneContract.RequireId(InputSnapshotId, nameof(InputSnapshotId));
    public string CodeSnapshotId { get; init; } = SceneContract.RequireId(CodeSnapshotId, nameof(CodeSnapshotId));
    public string CodeIndexId { get; init; } = SceneContract.RequireId(CodeIndexId, nameof(CodeIndexId));
    public string ParserId { get; init; } = SceneContract.RequireId(ParserId, nameof(ParserId));
    public string ContainerManifestDigest { get; init; } = SceneContract.RequireLowerCaseSha256(ContainerManifestDigest, nameof(ContainerManifestDigest));
}

public sealed record SceneContainerRecord(
    string ContainerId,
    string SceneSnapshotId,
    string RelativePath,
    string ContainerKind,
    string UnityVersion,
    int SerializedFileVersion,
    long ByteCount,
    string Sha256,
    string SidecarManifest)
{
    public string ContainerId { get; init; } = SceneContract.RequireId(ContainerId, nameof(ContainerId));
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string Sha256 { get; init; } = SceneContract.RequireLowerCaseSha256(Sha256, nameof(Sha256));
}

public sealed record SceneDocumentRecord(
    string SceneId,
    string SceneSnapshotId,
    string ContainerId,
    SceneDocumentKind Kind,
    string Name,
    long? SourceLocalFileId,
    int ObjectCount,
    int RootCount,
    SceneRecoveryStatus RecoveryStatus)
{
    public string SceneId { get; init; } = SceneContract.RequireId(SceneId, nameof(SceneId));
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string ContainerId { get; init; } = SceneContract.RequireId(ContainerId, nameof(ContainerId));
    public long? SourceLocalFileId { get; init; } = SceneContract.RequireOptionalPositiveLocalFileId(SourceLocalFileId, nameof(SourceLocalFileId));
}

public sealed record SceneGameObjectRecord(
    string GameObjectId,
    string SceneId,
    string ContainerId,
    long LocalFileId,
    string Name,
    bool? Active,
    int? Layer,
    string? Tag,
    SceneRecoveryStatus RecoveryStatus)
{
    public string GameObjectId { get; init; } = SceneContract.RequireId(GameObjectId, nameof(GameObjectId));
    public string SceneId { get; init; } = SceneContract.RequireId(SceneId, nameof(SceneId));
    public string ContainerId { get; init; } = SceneContract.RequireId(ContainerId, nameof(ContainerId));
    public long LocalFileId { get; init; } = SceneContract.RequirePositiveLocalFileId(LocalFileId, nameof(LocalFileId));
}

public sealed record SceneTransformRecord(
    string GameObjectId,
    string? ParentGameObjectId,
    int? SiblingIndex,
    float? PositionX,
    float? PositionY,
    float? PositionZ,
    float? RotationX,
    float? RotationY,
    float? RotationZ,
    float? RotationW,
    float? ScaleX,
    float? ScaleY,
    float? ScaleZ,
    SceneRecoveryStatus RecoveryStatus)
{
    public string GameObjectId { get; init; } = SceneContract.RequireId(GameObjectId, nameof(GameObjectId));
    public string? ParentGameObjectId { get; init; } = SceneContract.RequireOptionalId(ParentGameObjectId, nameof(ParentGameObjectId));
}

public sealed record SceneComponentRecord(
    string ComponentId,
    string GameObjectId,
    string ContainerId,
    long LocalFileId,
    int UnityClassId,
    string Kind,
    string? ScriptAssembly,
    string? ScriptNamespace,
    string? ScriptClass,
    string? ResolvedTypeSymbolId,
    string? ResolvedCodeIndexId,
    SceneResolutionStatus TypeResolutionStatus,
    SceneRecoveryStatus RecoveryStatus)
{
    public string ComponentId { get; init; } = SceneContract.RequireId(ComponentId, nameof(ComponentId));
    public string GameObjectId { get; init; } = SceneContract.RequireId(GameObjectId, nameof(GameObjectId));
    public string ContainerId { get; init; } = SceneContract.RequireId(ContainerId, nameof(ContainerId));
    public long LocalFileId { get; init; } = SceneContract.RequirePositiveLocalFileId(LocalFileId, nameof(LocalFileId));
    public string? ResolvedTypeSymbolId { get; init; } = SceneContract.RequireOptionalId(ResolvedTypeSymbolId, nameof(ResolvedTypeSymbolId));
    public string? ResolvedCodeIndexId { get; init; } = SceneContract.RequireOptionalId(ResolvedCodeIndexId, nameof(ResolvedCodeIndexId));
}

public sealed record SceneReferenceRecord(
    string ReferenceId,
    string SceneSnapshotId,
    string? SourceComponentId,
    string? FieldPath,
    string? DeclaredType,
    string SourceContainerId,
    long SourceLocalFileId,
    string? TargetContainerId,
    long? TargetLocalFileId,
    string? TargetGameObjectId,
    string? TargetComponentId,
    string? TargetSymbolId,
    string? TargetText,
    SceneResolutionStatus ResolutionStatus,
    string Evidence,
    SceneRecoveryStatus RecoveryStatus)
{
    public string ReferenceId { get; init; } = SceneContract.RequireId(ReferenceId, nameof(ReferenceId));
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SourceComponentId { get; init; } = SceneContract.RequireOptionalId(SourceComponentId, nameof(SourceComponentId));
    public string SourceContainerId { get; init; } = SceneContract.RequireId(SourceContainerId, nameof(SourceContainerId));
    public long SourceLocalFileId { get; init; } = SceneContract.RequirePositiveLocalFileId(SourceLocalFileId, nameof(SourceLocalFileId));
    public string? TargetContainerId { get; init; } = SceneContract.RequireOptionalId(TargetContainerId, nameof(TargetContainerId));
    public long? TargetLocalFileId { get; init; } = SceneContract.RequireOptionalPositiveLocalFileId(TargetLocalFileId, nameof(TargetLocalFileId));
    public string? TargetGameObjectId { get; init; } = SceneContract.RequireOptionalId(TargetGameObjectId, nameof(TargetGameObjectId));
    public string? TargetComponentId { get; init; } = SceneContract.RequireOptionalId(TargetComponentId, nameof(TargetComponentId));
    public string? TargetSymbolId { get; init; } = SceneContract.RequireOptionalId(TargetSymbolId, nameof(TargetSymbolId));
}

public sealed record SceneWriteSet(
    SceneSnapshotRecord Snapshot,
    IReadOnlyList<SceneContainerRecord> Containers,
    IReadOnlyList<SceneDocumentRecord> Documents,
    IReadOnlyList<SceneGameObjectRecord> GameObjects,
    IReadOnlyList<SceneTransformRecord> Transforms,
    IReadOnlyList<SceneComponentRecord> Components,
    IReadOnlyList<SceneReferenceRecord> References);

public sealed record SceneIndexStatistics(
    int ContainerCount,
    int DocumentCount,
    int GameObjectCount,
    int TransformCount,
    int ComponentCount,
    int ReferenceCount,
    IReadOnlyDictionary<string, int> RecoveryCounts);

internal static class SceneContract
{
    public static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    public static string? RequireOptionalId(string? value, string parameterName) =>
        value is null ? null : RequireId(value, parameterName);

    public static string RequireLowerCaseSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The value must be a lower-case SHA-256 digest.", parameterName);
        }

        return value;
    }

    public static long RequirePositiveLocalFileId(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }

    public static long? RequireOptionalPositiveLocalFileId(long? value, string parameterName) =>
        value is null ? null : RequirePositiveLocalFileId(value.Value, parameterName);
}
