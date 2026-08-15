using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Indexing.Scene;

namespace S1Atlas.Cli.Output;

internal sealed record SceneListOutput(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, int TotalCount, int ReturnedCount, IReadOnlyList<SceneDocumentRecord> Scenes);
internal sealed record SceneDocumentOutput(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneDocumentRecord? Scene, IReadOnlyList<SceneDocumentRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References);
internal sealed record GameObjectOutput(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneGameObjectRecord? GameObject, IReadOnlyList<SceneGameObjectRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References);
internal sealed record ComponentOutput(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneComponentRecord? Component, IReadOnlyList<SceneComponentRecord> Candidates, ScenePageResult<SceneReferenceRecord> References, SymbolQueryResult? CodeSymbol);
internal sealed record SceneIndexOutput(string SceneSnapshotId, string BuildId, string ParserId, string ParserVersion, int DocumentCount, int GameObjectCount, int ComponentCount, int ReferenceCount, IReadOnlyDictionary<string, int> RecoveryCounts, IReadOnlyList<string> Warnings);
