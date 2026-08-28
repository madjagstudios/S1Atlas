using S1Atlas.Core.ReferenceMods;
using S1Atlas.Indexing.ReferenceMods;
using Xunit;

namespace S1Atlas.Indexing.Tests.ReferenceMods;

public sealed class ReferenceModManifestLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-mod-manifest-" + Guid.NewGuid().ToString("N"));

    public ReferenceModManifestLoaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task LoadAsync_normalizes_a_valid_qol_manifest()
    {
        var modRoot = Directory.CreateDirectory(Path.Combine(_root, "mods", "ChemicalPlant")).FullName;
        var manifestPath = WriteManifest(
            """
            {
              "collection": "QOL",
              "mods": [
                {
                  "id": "Chemical-Plant",
                  "displayName": "Chemical Plant",
                  "rootPath": "__ROOT__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.txt", "**/*.dll", "**/*.cs", "**/*.md"],
                  "exclude": ["**/obj/**", "**/BepInEx/cache/**", "**/bin/**"]
                }
              ]
            }
            """.Replace("__ROOT__", JsonEscape(modRoot), StringComparison.Ordinal));

        var collection = await new ReferenceModManifestLoader().LoadAsync(
            manifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, collection.BuildId);
        Assert.Equal(string.Empty, collection.GameIndexId);
        Assert.Equal("qol", collection.CollectionId);
        Assert.Null(collection.CollectionName);

        var mod = Assert.Single(collection.Mods);
        Assert.Equal("chemical-plant", mod.ModId);
        Assert.Equal("Chemical Plant", mod.DisplayName);
        Assert.Equal("local", mod.Version);
        Assert.Equal("MIT", mod.License);
        Assert.Equal(Path.GetFullPath(modRoot), mod.RootPath);
        Assert.Equal(string.Empty, mod.ContentSha256);
        Assert.Equal(["**/*.cs", "**/*.dll", "**/*.md", "**/*.txt"], mod.Include);
        Assert.Equal(["**/BepInEx/cache/**", "**/bin/**", "**/obj/**"], mod.Exclude);
    }

    [Fact]
    public async Task LoadAsync_rejects_duplicate_mod_ids_after_normalization()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_root, "mods", "First")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_root, "mods", "Second")).FullName;
        var manifestPath = WriteManifest(
            """
            {
              "collection": "qol",
              "mods": [
                {
                  "id": "Same-Mod",
                  "displayName": "First",
                  "rootPath": "__FIRST__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.cs"]
                },
                {
                  "id": "same-mod",
                  "displayName": "Second",
                  "rootPath": "__SECOND__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.cs"]
                }
              ]
            }
            """
                .Replace("__FIRST__", JsonEscape(firstRoot), StringComparison.Ordinal)
                .Replace("__SECOND__", JsonEscape(secondRoot), StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReferenceModManifestLoader().LoadAsync(
                manifestPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("mods[1].id", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "", "displayName": "Mod", "rootPath": "__ROOT__", "version": "local", "license": "MIT", "include": ["**/*.cs"] } ] }""", "mods[0].id")]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "mod-a", "displayName": "", "rootPath": "__ROOT__", "version": "local", "license": "MIT", "include": ["**/*.cs"] } ] }""", "mods[0].displayName")]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "mod-a", "displayName": "Mod", "rootPath": "__ROOT__", "version": "local", "license": "", "include": ["**/*.cs"] } ] }""", "mods[0].license")]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "mod-a", "displayName": "Mod", "rootPath": "__ROOT__", "version": "local", "license": "MIT", "include": [] } ] }""", "mods[0].include")]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "mod-a", "displayName": "Mod", "rootPath": "__ROOT__", "version": "local", "license": "MIT", "include": ["../*.cs"] } ] }""", "mods[0].include[0]")]
    [InlineData("""{ "collection": "qol", "mods": [ { "id": "mod-a", "displayName": "Mod", "rootPath": "__ROOT__", "version": "local", "license": "MIT", "include": ["**/*.cs"], "exclude": ["http://example.com/*"] } ] }""", "mods[0].exclude[0]")]
    public async Task LoadAsync_rejects_invalid_manifest_fields(string template, string expectedField)
    {
        var modRoot = Directory.CreateDirectory(Path.Combine(_root, "mods", Guid.NewGuid().ToString("N"))).FullName;
        var manifestPath = WriteManifest(template.Replace("__ROOT__", JsonEscape(modRoot), StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReferenceModManifestLoader().LoadAsync(
                manifestPath,
                TestContext.Current.CancellationToken));

        Assert.Contains(expectedField, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""\\server\share\mod""")]
    [InlineData("""https://example.com/mod.zip""")]
    [InlineData("""file:///C:/mods/mod-a""")]
    [InlineData("""..\mods\escape""")]
    public async Task LoadAsync_rejects_network_like_or_escaping_roots(string rootPath)
    {
        var manifestPath = WriteManifest(
            """
            {
              "collection": "qol",
              "mods": [
                {
                  "id": "mod-a",
                  "displayName": "Mod A",
                  "rootPath": "__ROOT__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.cs"]
                }
              ]
            }
            """.Replace("__ROOT__", JsonEscape(rootPath), StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReferenceModManifestLoader().LoadAsync(
                manifestPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("mods[0].rootPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_rejects_missing_mod_root_directory()
    {
        var missing = Path.Combine(_root, "mods", "Missing");
        var manifestPath = WriteManifest(
            """
            {
              "collection": "qol",
              "mods": [
                {
                  "id": "mod-a",
                  "displayName": "Mod A",
                  "rootPath": "__ROOT__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.cs"]
                }
              ]
            }
            """.Replace("__ROOT__", JsonEscape(missing), StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReferenceModManifestLoader().LoadAsync(
                manifestPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("mods[0].rootPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_rejects_unmapped_json_properties()
    {
        var modRoot = Directory.CreateDirectory(Path.Combine(_root, "mods", "Strict")).FullName;
        var manifestPath = WriteManifest(
            """
            {
              "collection": "qol",
              "unexpected": true,
              "mods": [
                {
                  "id": "mod-a",
                  "displayName": "Mod A",
                  "rootPath": "__ROOT__",
                  "version": "local",
                  "license": "MIT",
                  "include": ["**/*.cs"],
                  "extraField": "nope"
                }
              ]
            }
            """.Replace("__ROOT__", JsonEscape(modRoot), StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReferenceModManifestLoader().LoadAsync(
                manifestPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("unexpected", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);
}
