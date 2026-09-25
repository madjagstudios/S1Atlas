using AssetsTools.NET;
using S1Atlas.Core.Scenes;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class ScriptFieldParserTests
{
    private static readonly UnityClassDatabaseDescriptor Descriptor = new("fixture-classdata", "1", new string('c', 64));

    private static SceneScriptLayoutSource Layouts => new(ScriptLayoutFixturePaths.ManagedDirectory, "fixture-layouts");

    [Fact]
    public async Task Attributed_script_decodes_private_serialized_and_public_fields_byte_exact()
    {
        var shop = await ParseObjectAsync(103);

        var fields = Assert.IsType<ParsedScriptFields>(shop.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Decoded, fields.Status);
        Assert.False(fields.Truncated);
        Assert.Contains(fields.Fields, field => field is { Path: "Price", Kind: SceneScriptFieldValueKind.Integer, Value: "50000" });
        Assert.Contains(fields.Fields, field => field is { Path: "Wage", Kind: SceneScriptFieldValueKind.Float, Value: "100.5" });
        Assert.Contains(fields.Fields, field => field is { Path: "Label", Kind: SceneScriptFieldValueKind.String, Value: "Docks" });
        Assert.Contains(fields.Fields, field => field is { Path: "Stock.Array.size", Kind: SceneScriptFieldValueKind.ArraySize, Value: "3" });
        Assert.Contains(fields.Fields, field => field is { Path: "Stock.Array[2]", Value: "3" });
        Assert.DoesNotContain(fields.Fields, field => field.Path is "Ignored" or "NotSerialized" or "m_Name" or "m_Script");
    }

    [Fact]
    public async Task Asset_level_script_decodes_and_keeps_its_name()
    {
        var catalog = await ParseObjectAsync(110);

        Assert.Equal("SCD_Fixture", catalog.MonoBehaviour!.Name);
        Assert.Equal(0, catalog.MonoBehaviour.GameObject.LocalFileId);
        var fields = Assert.IsType<ParsedScriptFields>(catalog.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Decoded, fields.Status);
        Assert.Contains(fields.Fields, field => field is { Path: "MaxQuantity", Value: "100" });
        Assert.Contains(fields.Fields, field => field is { Path: "Tint.a", Value: "1" });
    }

    [Fact]
    public async Task Layout_missing_a_serialized_field_is_rejected_by_the_byte_check_not_decoded_as_garbage()
    {
        var shop = await ParseObjectAsync(112);

        var fields = Assert.IsType<ParsedScriptFields>(shop.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Unavailable, fields.Status);
        Assert.StartsWith("layout-mismatch", fields.UnavailableReason);
        Assert.Empty(fields.Fields);
    }

    [Fact]
    public async Task Corrupt_array_length_fails_inside_the_object_slice_without_allocating_it()
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var inventory = await ParseObjectAsync(113);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        var fields = Assert.IsType<ParsedScriptFields>(inventory.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Unavailable, fields.Status);
        Assert.StartsWith("layout-mismatch", fields.UnavailableReason);
        Assert.True(allocated < 512L * 1024 * 1024, $"decode allocated {allocated} bytes");
    }

    [Fact]
    public async Task Byte_arrays_are_stored_as_hex_and_long_ones_mark_the_set_truncated()
    {
        var blob = await ParseObjectAsync(118);

        var fields = Assert.IsType<ParsedScriptFields>(blob.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Decoded, fields.Status);
        Assert.Contains(fields.Fields, field => field is { Path: "Small.Array", Kind: SceneScriptFieldValueKind.Bytes, Value: "01abff" });
        var large = Assert.Single(fields.Fields, field => field.Path == "Large.Array");
        Assert.Equal(SceneScriptFieldValueKind.Bytes, large.Kind);
        Assert.Equal(AssetsToolsUnitySerializedFileParser.MaxScriptStringLength, large.Value.Length);
        Assert.StartsWith("000102", large.Value, StringComparison.Ordinal);
        Assert.True(fields.Truncated);
    }

    [Fact]
    public async Task Script_class_missing_from_the_assembly_is_unavailable()
    {
        var missing = await ParseObjectAsync(114);

        var fields = Assert.IsType<ParsedScriptFields>(missing.ScriptFields);
        Assert.Equal(SceneScriptFieldSetStatus.Unavailable, fields.Status);
        Assert.Equal("script-type-not-found: Fixture.Game.DoesNotExist", fields.UnavailableReason);
    }

    [Fact]
    public async Task Without_script_layouts_no_field_sets_are_produced()
    {
        using var fixture = ScriptFieldSerializedFileFixture.Create();
        var container = Assert.Single(await Parser().ParseAsync([fixture.VerifiedContainer], null, TestContext.Current.CancellationToken));

        Assert.All(container.Objects, item => Assert.Null(item.ScriptFields));
        Assert.Equal("SCD_Fixture", container.Objects.Single(item => item.LocalFileId == 110).MonoBehaviour!.Name);
    }

    private static async Task<ParsedSceneObject> ParseObjectAsync(long pathId)
    {
        using var fixture = ScriptFieldSerializedFileFixture.Create();
        var container = Assert.Single(await Parser().ParseAsync([fixture.VerifiedContainer], Layouts, TestContext.Current.CancellationToken));
        return container.Objects.Single(item => item.LocalFileId == pathId);
    }

    private static AssetsToolsUnitySerializedFileParser Parser() => new(new FixtureClassDatabaseSource());

    private sealed class FixtureClassDatabaseSource : AssetsToolsUnitySerializedFileParser.IClassDatabaseSource
    {
        private readonly ClassDatabaseFile _database = ScriptFieldSerializedFileFixture.CreateClassDatabase();

        public UnityClassDatabaseDescriptor Descriptor => ScriptFieldParserTests.Descriptor;

        public AssetsToolsUnitySerializedFileParser.ClassDatabaseResolution? Resolve(string unityVersion) =>
            new(_database, unityVersion, true);
    }
}
