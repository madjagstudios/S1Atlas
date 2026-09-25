using S1Atlas.Core.Scenes;
using Xunit;

namespace S1Atlas.Core.Tests.Scenes;

public sealed class SceneScriptFieldModelTests
{
    [Fact]
    public void Decoded_field_set_rejects_an_unavailable_reason()
    {
        Assert.Throws<ArgumentException>(() => new SceneScriptFieldSetRecord(
            "owner", "snapshot", SceneScriptFieldOwnerKind.Component, SceneScriptFieldSetStatus.Decoded, "reason", false, []));
    }

    [Fact]
    public void Unavailable_field_set_requires_a_reason_and_no_fields()
    {
        Assert.Throws<ArgumentException>(() => new SceneScriptFieldSetRecord(
            "owner", "snapshot", SceneScriptFieldOwnerKind.Component, SceneScriptFieldSetStatus.Unavailable, null, false, []));
        Assert.Throws<ArgumentException>(() => new SceneScriptFieldSetRecord(
            "owner", "snapshot", SceneScriptFieldOwnerKind.Component, SceneScriptFieldSetStatus.Unavailable, "reason", false,
            [new SceneScriptField("Price", "int", SceneScriptFieldValueKind.Integer, "1")]));
    }

    [Fact]
    public void Script_field_requires_a_path()
    {
        Assert.Throws<ArgumentException>(() => new SceneScriptField(" ", "int", SceneScriptFieldValueKind.Integer, "1"));
    }

    [Fact]
    public void Only_pointer_fields_carry_a_target()
    {
        Assert.Null(new SceneScriptField("Price", "int", SceneScriptFieldValueKind.Integer, "1").Target);
        Assert.Throws<ArgumentException>(() => new SceneScriptField("Price", "int", SceneScriptFieldValueKind.Integer, "1",
            new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Null)));
    }

    [Fact]
    public void Target_status_requires_its_fields()
    {
        Assert.Throws<ArgumentException>(() => new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Resolved));
        Assert.Throws<ArgumentException>(() => new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Unresolved));
        var resolved = new SceneScriptFieldTarget(SceneScriptFieldTargetStatus.Resolved, SceneScriptFieldTargetKind.ScriptableAsset,
            "asset", "Diesel", "ScheduleOne.NPCs.NPCDataObject", "container", 6973);
        Assert.Equal("Diesel", resolved.Name);
    }

    [Fact]
    public void Scriptable_asset_requires_a_positive_local_file_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneScriptableAssetRecord(
            "asset", "snapshot", "container", 0, "SCD", null, null, null, null, null,
            SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly));
    }

    [Fact]
    public void Write_set_defaults_new_collections_to_empty()
    {
        var snapshot = new SceneSnapshotRecord("s", "b", "e", "i", "c", "x", "p", "v", new string('a', 64),
            SceneSnapshotStatus.Running, SceneRecoveryStatus.Unknown, "2026-09-24T00:00:00Z");
        var writeSet = new SceneWriteSet(snapshot, [], [], [], [], [], []);

        Assert.Empty(writeSet.ScriptableAssets);
        Assert.Empty(writeSet.ScriptFieldSets);
        Assert.Null(snapshot.ScriptLayoutSource);
    }
}
