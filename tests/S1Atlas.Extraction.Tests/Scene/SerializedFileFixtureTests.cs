using System.Text;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class SerializedFileFixtureTests
{
    [Fact]
    public async Task Scene_graph_fixture_exposes_sanitized_graph_and_reference_cases_through_the_real_parser()
    {
        using var fixture = SerializedFileFixtureBuilder.CreateSceneGraph();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var containers = await parser.ParseAsync(
            fixture.VerifiedContainers,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, containers.Count);
        Assert.All(containers, container =>
        {
            Assert.Equal("2022.3.62f1", container.UnityVersion);
            Assert.Equal(22, container.SerializedFileVersion);
            Assert.False(container.HasPrefabEvidence);
        });

        var scene = containers.Single(container =>
            container.RelativePath.EndsWith("/level0", StringComparison.Ordinal));
        var root = Assert.Single(scene.Objects, item => item.LocalFileId == 101);
        Assert.Equal(ParsedSceneObjectKind.GameObject, root.Kind);
        Assert.Contains(
            Assert.IsType<ParsedGameObjectData>(root.GameObject).Components,
            pointer => pointer == new ParsedScenePPtr(0, 107));
        Assert.Contains(
            scene.Objects,
            item => item.LocalFileId == 102 && item.Kind == ParsedSceneObjectKind.Transform);
        Assert.Contains(
            scene.Objects,
            item => item.LocalFileId == 107 &&
                    item.UnityClassId == 23 &&
                    item.Kind == ParsedSceneObjectKind.Other);

        var behaviour = Assert.Single(scene.Objects, item => item.LocalFileId == 103);
        Assert.Equal(ParsedSceneObjectKind.MonoBehaviour, behaviour.Kind);
        Assert.Contains(
            scene.Objects,
            item => item.LocalFileId == 104 && item.Kind == ParsedSceneObjectKind.MonoScript);
        Assert.Contains(
            behaviour.References,
            reference => reference.FieldPath == "m_LocalTarget" &&
                         reference.Target == new ParsedScenePPtr(0, 101));
        Assert.Contains(
            behaviour.References,
            reference => reference.FieldPath == "m_ExternalTarget" &&
                         reference.Target == new ParsedScenePPtr(1, 101));
        Assert.Contains(
            behaviour.References,
            reference => reference.FieldPath == "m_MissingTarget" &&
                         reference.Target == new ParsedScenePPtr(2, 999));

        Assert.Contains(
            containers.Single(container =>
                container.RelativePath.EndsWith("/sharedassets0.assets", StringComparison.Ordinal)).Objects,
            item => item.LocalFileId == 101 && item.Kind == ParsedSceneObjectKind.GameObject);
        Assert.Equal(2, scene.ExternalReferences.Count);

        foreach (var path in fixture.SerializedFilePaths)
        {
            var printableBytes = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken));
            Assert.DoesNotContain("ScheduleOne", printableBytes, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Schedule I", printableBytes, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Prefab_fixture_exposes_parser_certified_class_id_evidence()
    {
        using var fixture = SerializedFileFixtureBuilder.CreatePrefabEvidence();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            fixture.VerifiedContainers,
            TestContext.Current.CancellationToken));

        Assert.True(container.HasPrefabEvidence);
        var evidence = Assert.Single(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.PrefabEvidence);
        Assert.Equal(1001, evidence.UnityClassId);
        Assert.Empty(evidence.References);
        Assert.Contains(
            container.Objects,
            item => item.LocalFileId == 101 && item.Kind == ParsedSceneObjectKind.GameObject);
    }
}
