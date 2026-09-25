namespace S1Atlas.Core.Scenes;

public enum SceneDocumentKind { Scene, Prefab }
public enum SceneSnapshotStatus { Running, Completed, Failed }
public enum SceneResolutionStatus { Resolved, UnresolvedText, Ambiguous, NotIndexed, Unavailable }
public enum SceneScriptFieldOwnerKind { Component, ScriptableAsset }
public enum SceneScriptFieldSetStatus { Decoded, Unavailable }
public enum SceneScriptFieldValueKind { Integer, Float, Boolean, String, PPtr, ArraySize, Bytes }
public enum SceneScriptFieldTargetStatus { Resolved, Null, Unresolved }
public enum SceneScriptFieldTargetKind { GameObject, Component, ScriptableAsset, MonoScript, Asset }

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
    string? FailureMessage = null,
    string? TypeTreeSource = null,
    string? ScriptLayoutSource = null)
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

/// <summary>
/// An asset-level MonoBehaviour (a ScriptableObject or other script asset whose m_GameObject is
/// null). It is not a component attachment, so it has no GameObject owner.
/// </summary>
public sealed record SceneScriptableAssetRecord(
    string AssetId,
    string SceneSnapshotId,
    string ContainerId,
    long LocalFileId,
    string Name,
    string? ScriptAssembly,
    string? ScriptNamespace,
    string? ScriptClass,
    string? ResolvedTypeSymbolId,
    string? ResolvedCodeIndexId,
    SceneResolutionStatus TypeResolutionStatus,
    SceneRecoveryStatus RecoveryStatus)
{
    public string AssetId { get; init; } = SceneContract.RequireId(AssetId, nameof(AssetId));
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string ContainerId { get; init; } = SceneContract.RequireId(ContainerId, nameof(ContainerId));
    public long LocalFileId { get; init; } = SceneContract.RequirePositiveLocalFileId(LocalFileId, nameof(LocalFileId));
    public string Name { get; init; } = Name ?? throw new ArgumentNullException(nameof(Name));
    public string? ResolvedTypeSymbolId { get; init; } = SceneContract.RequireOptionalId(ResolvedTypeSymbolId, nameof(ResolvedTypeSymbolId));
    public string? ResolvedCodeIndexId { get; init; } = SceneContract.RequireOptionalId(ResolvedCodeIndexId, nameof(ResolvedCodeIndexId));
}

/// <summary>
/// One flattened serialized leaf value, as invariant-culture text. A PPtr value keeps its raw
/// "fileId:localFileId" text; <see cref="Target"/> says what it points to in the same snapshot.
/// </summary>
public sealed record SceneScriptField(
    string Path,
    string TypeName,
    SceneScriptFieldValueKind Kind,
    string Value,
    SceneScriptFieldTarget? Target = null)
{
    public string Path { get; init; } = SceneContract.RequireId(Path, nameof(Path));
    public string TypeName { get; init; } = SceneContract.RequireId(TypeName, nameof(TypeName));
    public string Value { get; init; } = Value ?? throw new ArgumentNullException(nameof(Value));
    public SceneScriptFieldTarget? Target { get; init; } = Target is null || Kind == SceneScriptFieldValueKind.PPtr
        ? Target
        : throw new ArgumentException("Only a PPtr field has a target.", nameof(Target));
}

/// <summary>
/// The object a PPtr field value points to. <see cref="Id"/> is the indexed record ID when the
/// target is an indexed GameObject, component or scriptable asset.
/// </summary>
public sealed record SceneScriptFieldTarget(
    SceneScriptFieldTargetStatus Status,
    SceneScriptFieldTargetKind? Kind = null,
    string? Id = null,
    string? Name = null,
    string? TypeName = null,
    string? ContainerId = null,
    long? LocalFileId = null,
    string? Reason = null)
{
    public SceneScriptFieldTargetKind? Kind { get; init; } = Status == SceneScriptFieldTargetStatus.Resolved && (Kind is null || ContainerId is null || LocalFileId is null)
        ? throw new ArgumentException("A resolved target needs a kind, container and local file ID.", nameof(Kind))
        : Kind;
    public string? Reason { get; init; } = Status == SceneScriptFieldTargetStatus.Unresolved && string.IsNullOrWhiteSpace(Reason)
        ? throw new ArgumentException("An unresolved target needs a reason.", nameof(Reason))
        : Reason;
}

/// <summary>
/// The decoded serialized fields of one component or scriptable asset, or the reason they are
/// unavailable.
/// </summary>
public sealed record SceneScriptFieldSetRecord(
    string OwnerId,
    string SceneSnapshotId,
    SceneScriptFieldOwnerKind OwnerKind,
    SceneScriptFieldSetStatus Status,
    string? UnavailableReason,
    bool Truncated,
    IReadOnlyList<SceneScriptField> Fields)
{
    public string OwnerId { get; init; } = SceneContract.RequireId(OwnerId, nameof(OwnerId));
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? UnavailableReason { get; init; } = RequireReason(Status, UnavailableReason, Fields);
    public IReadOnlyList<SceneScriptField> Fields { get; init; } = Fields ?? throw new ArgumentNullException(nameof(Fields));

    private static string? RequireReason(SceneScriptFieldSetStatus status, string? reason, IReadOnlyList<SceneScriptField>? fields)
    {
        if (status == SceneScriptFieldSetStatus.Decoded && reason is not null)
            throw new ArgumentException("A decoded field set has no unavailable reason.", nameof(UnavailableReason));
        if (status == SceneScriptFieldSetStatus.Unavailable && (string.IsNullOrWhiteSpace(reason) || fields is { Count: > 0 }))
            throw new ArgumentException("An unavailable field set needs a reason and no fields.", nameof(UnavailableReason));
        return reason;
    }
}

public sealed record SceneWriteSet(
    SceneSnapshotRecord Snapshot,
    IReadOnlyList<SceneContainerRecord> Containers,
    IReadOnlyList<SceneDocumentRecord> Documents,
    IReadOnlyList<SceneGameObjectRecord> GameObjects,
    IReadOnlyList<SceneTransformRecord> Transforms,
    IReadOnlyList<SceneComponentRecord> Components,
    IReadOnlyList<SceneReferenceRecord> References,
    IReadOnlyList<SceneScriptableAssetRecord>? ScriptableAssets = null,
    IReadOnlyList<SceneScriptFieldSetRecord>? ScriptFieldSets = null)
{
    public IReadOnlyList<SceneScriptableAssetRecord> ScriptableAssets { get; init; } = ScriptableAssets ?? [];
    public IReadOnlyList<SceneScriptFieldSetRecord> ScriptFieldSets { get; init; } = ScriptFieldSets ?? [];
}

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
