using AssetsTools.NET;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

// a stripped-type-tree container (TypeTreeEnabled=false) decodes through the pinned
// class database when one matches, and stays an undecoded stub when none is configured.
public sealed class ClassDatabaseParserTests
{
    private static readonly UnityClassDatabaseDescriptor Descriptor = new(
        "unity-classdata",
        "fixture-1",
        new string('c', 64));

    [Fact]
    public async Task ParseAsync_StrippedContainer_WithMatchingClassDatabase_DecodesNamesHierarchyAndComponents()
    {
        using var stripped = SanitizedSerializedFileFixture.Create(includeTypeTree: false);
        var parser = new AssetsToolsUnitySerializedFileParser(new FixtureClassDatabaseSource(exact: true));

        var container = Assert.Single(await parser.ParseAsync([stripped.VerifiedContainer], TestContext.Current.CancellationToken));

        Assert.False(container.TypeTreeEmbedded);
        Assert.Equal(ParsedTypeTreeSourceKind.ClassDatabase, container.DecodeSource.Kind);
        Assert.Same(Descriptor, container.DecodeSource.ClassDatabase);
        Assert.Equal("2022.3.62f1", container.DecodeSource.ResolvedUnityVersion);
        Assert.True(container.DecodeSource.ExactVersionMatch);
        Assert.Equal(
            "class-database unity-classdata fixture-1 sha256:" + new string('c', 64) + " (2022.3.62f1 exact)",
            container.DecodeSource.Label(container.UnityVersion));

        var root = Assert.Single(container.Objects, item => item.LocalFileId == 101);
        var gameObject = Assert.IsType<ParsedGameObjectData>(root.GameObject);
        Assert.Equal("Sanitized Root", gameObject.Name);
        Assert.Equal(7u, gameObject.Layer);
        Assert.Equal((ushort)3, gameObject.Tag);
        Assert.True(gameObject.IsActive);
        Assert.Equal([new ParsedScenePPtr(0, 102), new ParsedScenePPtr(0, 103), new ParsedScenePPtr(0, 107)], gameObject.Components);

        var child = Assert.Single(container.Objects, item => item.LocalFileId == 105);
        Assert.Equal("Sanitized Child", child.GameObject!.Name);

        var transform = Assert.IsType<ParsedTransformData>(Assert.Single(container.Objects, item => item.LocalFileId == 106).Transform);
        Assert.Equal(new ParsedScenePPtr(0, 105), transform.GameObject);
        Assert.Equal(new ParsedScenePPtr(0, 102), transform.ParentTransform);
        Assert.Equal(new ParsedSceneVector3(1.25f, 2.5f, 3.75f), transform.LocalPosition);
        Assert.Equal(1, transform.RootOrder);

        var behaviour = Assert.IsType<ParsedMonoBehaviourData>(Assert.Single(container.Objects, item => item.LocalFileId == 103).MonoBehaviour);
        Assert.Equal(new ParsedScenePPtr(0, 104), behaviour.Script);
        Assert.True(behaviour.Enabled);
        var script = Assert.IsType<ParsedMonoScriptData>(Assert.Single(container.Objects, item => item.LocalFileId == 104).MonoScript);
        Assert.Equal("SceneGraphBehaviour", script.ClassName);
        Assert.Equal("Fixture.Namespace", script.Namespace);
        Assert.Equal("Assembly-CSharp.dll", script.AssemblyName);
        var buildSettings = Assert.IsType<ParsedBuildSettingsData>(Assert.Single(container.Objects, item => item.LocalFileId == 108).BuildSettings);
        Assert.Equal(3, buildSettings.ScenePaths.Count);
        Assert.NotEmpty(Assert.Single(container.Objects, item => item.LocalFileId == 103).References);
    }

    [Fact]
    public async Task ParseAsync_StrippedContainer_WithNearestClassDatabase_LabelsTheSubstitutedVersion()
    {
        using var stripped = SanitizedSerializedFileFixture.Create(includeTypeTree: false);
        var parser = new AssetsToolsUnitySerializedFileParser(new FixtureClassDatabaseSource(exact: false, resolvedVersion: "2022.3.26f1"));

        var container = Assert.Single(await parser.ParseAsync([stripped.VerifiedContainer], TestContext.Current.CancellationToken));

        Assert.Equal(ParsedTypeTreeSourceKind.ClassDatabase, container.DecodeSource.Kind);
        Assert.False(container.DecodeSource.ExactVersionMatch);
        Assert.Equal("2022.3.26f1", container.DecodeSource.ResolvedUnityVersion);
        Assert.Contains("(2022.3.26f1 layouts for 2022.3.62f1; nearest earlier dump)", container.DecodeSource.Label(container.UnityVersion), StringComparison.Ordinal);
        Assert.Equal("Sanitized Root", Assert.Single(container.Objects, item => item.LocalFileId == 101).GameObject!.Name);
    }

    [Fact]
    public async Task ParseAsync_StrippedContainer_WithoutClassDatabase_StaysUnavailable()
    {
        using var stripped = SanitizedSerializedFileFixture.Create(includeTypeTree: false);

        var withoutSource = Assert.Single(await new AssetsToolsUnitySerializedFileParser().ParseAsync([stripped.VerifiedContainer], TestContext.Current.CancellationToken));
        var unresolved = Assert.Single(await new AssetsToolsUnitySerializedFileParser(new FixtureClassDatabaseSource(resolves: false)).ParseAsync([stripped.VerifiedContainer], TestContext.Current.CancellationToken));

        foreach (var container in new[] { withoutSource, unresolved })
        {
            Assert.False(container.TypeTreeEmbedded);
            Assert.Equal(ParsedTypeTreeSourceKind.Unavailable, container.DecodeSource.Kind);
            Assert.Equal("unavailable", container.DecodeSource.Label(container.UnityVersion));
            Assert.All(container.Objects, item => Assert.Null(item.GameObject));
            Assert.All(container.Objects, item => Assert.Null(item.Transform));
            Assert.All(container.Objects, item => Assert.Empty(item.References));
        }
    }

    [Fact]
    public async Task ParseAsync_EmbeddedContainer_IgnoresTheClassDatabase()
    {
        using var embedded = SanitizedSerializedFileFixture.Create(includeTypeTree: true);
        var source = new FixtureClassDatabaseSource(exact: true);
        var parser = new AssetsToolsUnitySerializedFileParser(source);

        var container = Assert.Single(await parser.ParseAsync([embedded.VerifiedContainer], TestContext.Current.CancellationToken));

        Assert.True(container.TypeTreeEmbedded);
        Assert.Equal(ParsedTypeTreeSourceKind.Embedded, container.DecodeSource.Kind);
        Assert.Equal("embedded", container.DecodeSource.Label(container.UnityVersion));
        Assert.Equal(0, source.ResolveCalls);
        Assert.Equal("Sanitized Root", Assert.Single(container.Objects, item => item.LocalFileId == 101).GameObject!.Name);
    }

    [Fact]
    public void Parser_ExposesTheConfiguredClassDatabaseDescriptor()
    {
        Assert.Null(new AssetsToolsUnitySerializedFileParser().ClassDatabase);
        Assert.Same(Descriptor, new AssetsToolsUnitySerializedFileParser(new FixtureClassDatabaseSource(exact: true)).ClassDatabase);
    }

    private sealed class FixtureClassDatabaseSource(bool exact = true, string? resolvedVersion = null, bool resolves = true) : AssetsToolsUnitySerializedFileParser.IClassDatabaseSource
    {
        private readonly ClassDatabaseFile _database = SanitizedSerializedFileFixture.CreateClassDatabase();

        public int ResolveCalls { get; private set; }

        public UnityClassDatabaseDescriptor Descriptor => ClassDatabaseParserTests.Descriptor;

        public AssetsToolsUnitySerializedFileParser.ClassDatabaseResolution? Resolve(string unityVersion)
        {
            ResolveCalls++;
            return resolves
                ? new AssetsToolsUnitySerializedFileParser.ClassDatabaseResolution(_database, resolvedVersion ?? unityVersion, exact)
                : null;
        }
    }
}
