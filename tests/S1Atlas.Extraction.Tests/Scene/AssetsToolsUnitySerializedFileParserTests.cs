using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class AssetsToolsUnitySerializedFileParserTests
{
    [Fact]
    public async Task ParseAsync_Unity2022Fixture_MapsHeaderAndSupportedObjectTableClassIds()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var containers = await parser.ParseAsync(
        [
            fixture.VerifiedContainer
        ], TestContext.Current.CancellationToken);

        var container = Assert.Single(containers);
        Assert.Equal("Schedule I_Data/level0", container.RelativePath);
        Assert.Equal(fixture.PrimaryPath, container.PrimaryPath);
        Assert.Empty(container.SidecarPaths);
        Assert.Equal(fixture.VerifiedContainer.Sha256, container.Sha256);
        Assert.Equal("2022.3.62f1", container.UnityVersion);
        Assert.Equal(22, container.SerializedFileVersion);
        Assert.Collection(
            container.Objects,
            item => AssertObject(item, 101, 1, ParsedSceneObjectKind.GameObject),
            item => AssertObject(item, 102, 4, ParsedSceneObjectKind.Transform),
            item => AssertObject(item, 103, 114, ParsedSceneObjectKind.MonoBehaviour),
            item => AssertObject(item, 104, 115, ParsedSceneObjectKind.MonoScript),
            item => AssertObject(item, 105, 1, ParsedSceneObjectKind.GameObject),
            item => AssertObject(item, 106, 4, ParsedSceneObjectKind.Transform),
            item => AssertObject(item, 108, 141, ParsedSceneObjectKind.BuildSettings));
        Assert.False(container.HasPrefabEvidence);
    }

    [Fact]
    public async Task ParseAsync_IndependentFixture_DecodesBuiltInRelationshipsAndValues()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        var root = Assert.Single(container.Objects, item => item.LocalFileId == 101);
        var gameObject = Assert.IsType<ParsedGameObjectData>(root.GameObject);
        Assert.Equal("Sanitized Root", gameObject.Name);
        Assert.Equal(7u, gameObject.Layer);
        Assert.Equal((ushort)3, gameObject.Tag);
        Assert.True(gameObject.IsActive);
        Assert.Equal([new ParsedScenePPtr(0, 102), new ParsedScenePPtr(0, 103)], gameObject.Components);

        var rootTransform = Assert.Single(container.Objects, item => item.LocalFileId == 102);
        var transform = Assert.IsType<ParsedTransformData>(rootTransform.Transform);
        Assert.Equal(new ParsedScenePPtr(0, 101), transform.GameObject);
        Assert.Equal(new ParsedScenePPtr(0, 0), transform.ParentTransform);
        Assert.Equal([new ParsedScenePPtr(0, 106)], transform.Children);
        Assert.Equal(new ParsedSceneVector3(1.25f, 2.5f, 3.75f), transform.LocalPosition);
        Assert.Equal(new ParsedSceneQuaternion(0f, 0f, 0f, 1f), transform.LocalRotation);
        Assert.Equal(new ParsedSceneVector3(1f, 1f, 1f), transform.LocalScale);
        Assert.Equal(0, transform.RootOrder);

        var childTransform = Assert.Single(container.Objects, item => item.LocalFileId == 106);
        Assert.Equal(
            new ParsedScenePPtr(0, 102),
            Assert.IsType<ParsedTransformData>(childTransform.Transform).ParentTransform);
    }

    [Fact]
    public async Task ParseAsync_IndependentFixture_DecodesMonoScriptIdentityAndAllPPtrs()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        var behaviourObject = Assert.Single(container.Objects, item => item.LocalFileId == 103);
        var behaviour = Assert.IsType<ParsedMonoBehaviourData>(behaviourObject.MonoBehaviour);
        Assert.Equal(new ParsedScenePPtr(0, 101), behaviour.GameObject);
        Assert.Equal(new ParsedScenePPtr(0, 104), behaviour.Script);
        Assert.True(behaviour.Enabled);
        Assert.Contains(behaviourObject.References, reference =>
            reference.FieldPath == "m_Target" &&
            reference.DeclaredType == "PPtr<GameObject>" &&
            reference.Target == new ParsedScenePPtr(1, 501));

        var scriptObject = Assert.Single(container.Objects, item => item.LocalFileId == 104);
        var script = Assert.IsType<ParsedMonoScriptData>(scriptObject.MonoScript);
        Assert.Equal("Sanitized.Fixture.dll", script.AssemblyName);
        Assert.Equal("S1Atlas.Fixture", script.Namespace);
        Assert.Equal("Sanitized.Component", script.ClassName);

        Assert.Contains(
            Assert.Single(container.Objects, item => item.LocalFileId == 101).References,
            reference => reference.FieldPath == "m_Component.Array[0].component" &&
                         reference.Target == new ParsedScenePPtr(0, 102));
    }

    [Fact]
    public async Task ParseAsync_IndependentFixture_DecodesBuildSettingsScenePaths()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        var buildSettingsObject = Assert.Single(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.BuildSettings);
        var buildSettings = Assert.IsType<ParsedBuildSettingsData>(buildSettingsObject.BuildSettings);
        Assert.Equal(
        [
            "Assets/Scenes/SanitizedBootstrap.unity",
            "Assets/Scenes/SanitizedWorld.unity",
            "Assets/Scenes/SanitizedInterior.unity"
        ], buildSettings.ScenePaths);
    }

    [Fact]
    public async Task ParseAsync_WithoutTypeTree_PreservesObjectTableWithoutInventedData()
    {
        using var fixture = SanitizedSerializedFileFixture.Create(includeTypeTree: false);
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        Assert.Equal(7, container.Objects.Count);
        Assert.All(container.Objects, item => Assert.Empty(item.References));
        Assert.All(container.Objects, item => Assert.Null(item.GameObject));
        Assert.All(container.Objects, item => Assert.Null(item.Transform));
        Assert.All(container.Objects, item => Assert.Null(item.MonoBehaviour));
        Assert.All(container.Objects, item => Assert.Null(item.MonoScript));
        Assert.All(container.Objects, item => Assert.Null(item.BuildSettings));
    }

    [Fact]
    public async Task ParseAsync_Unity2022Fixture_MapsExternalFileReferences()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        var external = Assert.Single(container.ExternalReferences);
        Assert.Equal(1, external.FileId);
        Assert.Equal("archive:/CAB-sanitized/CAB-sanitized", external.PathName);
        Assert.Equal(
            "archive:/CAB-sanitized/CAB-sanitized",
            external.OriginalPathName);
    }

    [Fact]
    public async Task ParseAsync_PrefabMarkerWithoutPrefabClassId_DoesNotClaimPrefabEvidence()
    {
        using var fixture = SanitizedSerializedFileFixture.Create(
            objectPayloadMarker: "Prefab PrefabInstance");
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        Assert.False(container.HasPrefabEvidence);
        Assert.DoesNotContain(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.PrefabEvidence);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(1001480554)]
    public async Task ParseAsync_PrefabClassId_RecordsPrefabEvidence(int prefabClassId)
    {
        using var fixture = SanitizedSerializedFileFixture.Create(
            prefabClassId: prefabClassId);
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        Assert.True(container.HasPrefabEvidence);
        var prefab = Assert.Single(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.PrefabEvidence);
        Assert.Equal(prefabClassId, prefab.UnityClassId);
    }

    private static void AssertObject(
        ParsedSceneObject item,
        long localFileId,
        int classId,
        ParsedSceneObjectKind kind)
    {
        Assert.Equal(localFileId, item.LocalFileId);
        Assert.Equal(classId, item.UnityClassId);
        Assert.Equal(kind, item.Kind);
        Assert.True(item.ByteCount > 0);
    }

}
