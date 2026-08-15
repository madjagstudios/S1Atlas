namespace S1Atlas.Indexing.Scene;

public sealed record SceneIndexWorkflowResult(
    string SceneSnapshotId,
    string BuildId,
    string CodeIndexId,
    string ParserId,
    string ParserVersion,
    bool Reused,
    int ContainerCount,
    int SceneCount,
    int GameObjectCount,
    int TransformCount,
    int ComponentCount,
    int ReferenceCount,
    IReadOnlyDictionary<string, int>? RecoveryCounts = null,
    IReadOnlyList<string>? Warnings = null);
