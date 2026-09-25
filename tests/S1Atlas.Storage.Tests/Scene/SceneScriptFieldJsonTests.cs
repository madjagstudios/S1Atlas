using S1Atlas.Core.Scenes;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Scene;

public sealed class SceneScriptFieldJsonTests
{
    [Fact]
    public void Targets_round_trip_with_nulls_omitted()
    {
        SceneScriptField[] fields =
        [
            new("Price", "int", SceneScriptFieldValueKind.Integer, "50000"),
            new("Leader.Data", "PPtr<$BaseNPCDataObject>", SceneScriptFieldValueKind.PPtr, "0:6973",
                new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Resolved, SceneScriptFieldTargetKind.ScriptableAsset,
                    "asset", "Diesel", "ScheduleOne.NPCs.NPCDataObject", "container", 6973)),
            new("Effects.Array[0]", "PPtr<$Effect>", SceneScriptFieldValueKind.PPtr, "9:1",
                new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Unresolved, Reason: "fileId=9;localFileId=1;external=<missing-table-entry>"))
        ];

        var json = SceneScriptFieldJson.Serialize(fields);

        Assert.Equal(fields, SceneScriptFieldJson.Deserialize(json));
        Assert.DoesNotContain("\"target\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_sets_stored_before_targets_existed_still_read()
    {
        var fields = SceneScriptFieldJson.Deserialize("""[{"path":"Leader.Data","typeName":"PPtr<$X>","kind":"PPtr","value":"0:6973"}]""");

        Assert.Null(Assert.Single(fields).Target);
    }
}
