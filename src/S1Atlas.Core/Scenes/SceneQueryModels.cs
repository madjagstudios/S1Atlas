namespace S1Atlas.Core.Scenes;

public sealed record SceneListQueryOptions(
    string SceneSnapshotId,
    SceneDocumentKind? Kind = null,
    string? Query = null,
    int Limit = 50)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record GameObjectListQueryOptions(
    string SceneSnapshotId,
    string? SceneId = null,
    string? ParentGameObjectId = null,
    string? Query = null,
    int Limit = 50)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? ParentGameObjectId { get; init; } = SceneContract.RequireOptionalId(ParentGameObjectId, nameof(ParentGameObjectId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);

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
    int Limit = 50)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? GameObjectId { get; init; } = SceneContract.RequireOptionalId(GameObjectId, nameof(GameObjectId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);

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
    int Limit = 50)
{
    public string SceneSnapshotId { get; init; } = SceneContract.RequireId(SceneSnapshotId, nameof(SceneSnapshotId));
    public string? SceneId { get; init; } = SceneContract.RequireOptionalId(SceneId, nameof(SceneId));
    public string? GameObjectId { get; init; } = SceneContract.RequireOptionalId(GameObjectId, nameof(GameObjectId));
    public string? SourceComponentId { get; init; } = SceneContract.RequireOptionalId(SourceComponentId, nameof(SourceComponentId));
    public int Limit { get; init; } = RequirePositiveLimit(Limit);

    private static int RequirePositiveLimit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record ScenePageResult<T>(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<T> Rows);
