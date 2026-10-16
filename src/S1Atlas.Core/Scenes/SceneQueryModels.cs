namespace S1Atlas.Core.Scenes;

public sealed record SceneListQueryOptions(
    string SceneSnapshotId,
    SceneDocumentKind? Kind = null,
    string? Query = null,
    int Limit = 50,
    int Offset = 0)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);
    public int Offset { get; init; } = RequireNonNegativeOffset(Offset);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }

    private static int RequireNonNegativeOffset(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value;
    }
}

public sealed record GameObjectListQueryOptions(
    string SceneSnapshotId,
    string? SceneId = null,
    string? ParentGameObjectId = null,
    string? Query = null,
    int Limit = 50,
    int Offset = 0)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? ParentGameObjectId { get; init; } = SceneContract.RequireOptionalId(ParentGameObjectId, nameof(ParentGameObjectId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);
    public int Offset { get; init; } = SceneContract.RequireNonNegativeOffset(Offset);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record ComponentListQueryOptions(
    string SceneSnapshotId,
    string? SceneId = null,
    string? GameObjectId = null,
    string? Query = null,
    string? ExactKind = null,
    int Limit = 50,
    int Offset = 0,
    string? ExactType = null,
    IReadOnlyList<string>? TypeSymbolIds = null)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? GameObjectId { get; init; } = SceneContract.RequireOptionalId(GameObjectId, nameof(GameObjectId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);
    public int Offset { get; init; } = SceneContract.RequireNonNegativeOffset(Offset);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record ReferenceListQueryOptions(
    string SceneSnapshotId,
    string? SceneId = null,
    string? GameObjectId = null,
    string? SourceComponentId = null,
    string? Query = null,
    int Limit = 50,
    int Offset = 0)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? GameObjectId { get; init; } = SceneContract.RequireOptionalId(GameObjectId, nameof(GameObjectId));
    public string? SourceComponentId { get; init; } = SceneContract.RequireOptionalId(SourceComponentId, nameof(SourceComponentId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);
    public int Offset { get; init; } = SceneContract.RequireNonNegativeOffset(Offset);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record ScenePageResult<T>(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<T> Rows,
    int UnresolvedCount = 0)
{
    public int TotalCount { get; init; } = RequireNonnegative(TotalCount, nameof(TotalCount));
    public int ReturnedCount { get; init; } = RequireNonnegative(ReturnedCount, nameof(ReturnedCount));
    public int UnresolvedCount { get; init; } = RequireUnresolvedCount(TotalCount, UnresolvedCount);
    public IReadOnlyList<T> Rows { get; init; } = RequireRows(TotalCount, ReturnedCount, Rows);

    private static int RequireNonnegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static int RequireUnresolvedCount(int totalCount, int unresolvedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unresolvedCount, nameof(unresolvedCount));
        if (unresolvedCount > totalCount)
            throw new ArgumentException("UnresolvedCount must not exceed TotalCount.", nameof(unresolvedCount));
        return unresolvedCount;
    }

    private static IReadOnlyList<T> RequireRows(int totalCount, int returnedCount, IReadOnlyList<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (returnedCount != rows.Count)
            throw new ArgumentException("ReturnedCount must equal Rows.Count.", nameof(returnedCount));
        if (returnedCount > totalCount)
            throw new ArgumentException("ReturnedCount must not exceed TotalCount.", nameof(returnedCount));

        return rows;
    }
}

/// <summary>
/// Incoming references: decoded script-field PPtrs anywhere in the snapshot whose resolved
/// target is one of <see cref="TargetIds"/>. A source owner inside the hierarchy rooted at
/// <see cref="HierarchyRootGameObjectId"/> (that object or a descendant) is internal.
/// </summary>
public sealed record IncomingReferenceQueryOptions(
    string SceneSnapshotId,
    IReadOnlyList<string> TargetIds,
    string HierarchyRootGameObjectId,
    bool ExternalOnly = false,
    int Limit = 50,
    int Offset = 0)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public IReadOnlyList<string> TargetIds { get; init; } = TargetIds is { Count: > 0 }
        ? TargetIds
        : throw new ArgumentException("At least one target ID is required.", nameof(TargetIds));
    public string HierarchyRootGameObjectId { get; init; } = SceneContract.RequireId(HierarchyRootGameObjectId, nameof(HierarchyRootGameObjectId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);
    public int Offset { get; init; } = SceneContract.RequireNonNegativeOffset(Offset);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

/// <summary>
/// One decoded script-field PPtr that points at the queried object. The source owner is a
/// component (with its GameObject) or a scriptable asset (no GameObject).
/// </summary>
public sealed record SceneIncomingReferenceRecord(
    string SourceOwnerId,
    SceneScriptFieldOwnerKind SourceOwnerKind,
    string? SourceGameObjectId,
    string? SourceName,
    string? SourceType,
    string SourceContainerId,
    string FieldPath,
    string FieldTypeName,
    string Value,
    string TargetId,
    SceneScriptFieldTargetKind TargetKind,
    bool WithinTarget,
    string Evidence = SceneIncomingReferenceRecord.ScriptFieldEvidence)
{
    public const string ScriptFieldEvidence = "SerializedScriptFieldPPtr";
}

/// <summary>
/// The script field sets an incoming-reference search read: decoded, unavailable (not decoded),
/// truncated (capped), and script owners with no field set because they were never parsed.
/// </summary>
public sealed record SceneFieldSetCoverage(int DecodedCount, int UnavailableCount, int TruncatedCount, int NotParsedCount);

public sealed record SceneIncomingReferencePage(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SceneIncomingReferenceRecord> Rows,
    int ExternalCount,
    SceneFieldSetCoverage Coverage);
