using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Indexing.ReferenceMods;
using Xunit;

namespace S1Atlas.Indexing.Tests.ReferenceMods;

public sealed class ReferenceModFileSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-mod-selector-" + Guid.NewGuid().ToString("N"));

    public ReferenceModFileSelectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Select_and_hash_returns_sorted_safe_inputs_and_path_independent_collection_hash()
    {
        var firstRoot = CreateModTree("first");
        var secondRoot = CreateModTree("second");

        var firstMod = CreateMod(firstRoot);
        var secondMod = CreateMod(secondRoot);

        var selected = new ReferenceModFileSelector().Select([secondMod, firstMod]);

        Assert.Equal(
            [
                ("chem-plant", "Docs/Guide.txt", ReferenceModInputKind.TextDocument, "Guide"),
                ("chem-plant", "Docs/Guide.txt", ReferenceModInputKind.TextDocument, "Guide"),
                ("chem-plant", "README.md", ReferenceModInputKind.TextDocument, "Readme"),
                ("chem-plant", "README.md", ReferenceModInputKind.TextDocument, "Readme"),
                ("chem-plant", "plugins/ChemPlant.dll", ReferenceModInputKind.ManagedAssembly, (string?)null),
                ("chem-plant", "plugins/ChemPlant.dll", ReferenceModInputKind.ManagedAssembly, (string?)null),
                ("chem-plant", "src/Feature.cs", ReferenceModInputKind.SourceText, "Source"),
                ("chem-plant", "src/Feature.cs", ReferenceModInputKind.SourceText, "Source")
            ],
            selected.Select(file => (file.ModId, file.RelativePath, file.Kind, file.DeclaredDocumentKind)).ToArray());

        var firstHash = await new ReferenceModInputHasher().HashAsync(
            selected,
            TestContext.Current.CancellationToken);
        var secondHash = await new ReferenceModInputHasher().HashAsync(
            new ReferenceModFileSelector().Select([CreateMod(secondRoot), CreateMod(firstRoot)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(firstHash.CollectionContentSha256, secondHash.CollectionContentSha256);
        Assert.All(firstHash.Files, fileHash => Assert.DoesNotContain(Path.GetPathRoot(fileHash.FullPath)!, firstHash.CollectionContentSha256, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HashAsync_changes_collection_hash_when_declared_mod_metadata_changes()
    {
        var modRoot = CreateModTree("metadata");
        var baseline = new ReferenceModFileSelector().Select([
            CreateMod(modRoot, version: "1.0.0", license: "MIT", displayName: "Chemical Plant")
        ]);
        var changed = new ReferenceModFileSelector().Select([
            CreateMod(modRoot, version: "1.0.1", license: "GPL-3.0-only", displayName: "Chemical Plant Reloaded")
        ]);

        var baselineHash = await new ReferenceModInputHasher().HashAsync(
            baseline,
            TestContext.Current.CancellationToken);
        var changedHash = await new ReferenceModInputHasher().HashAsync(
            changed,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(baselineHash.CollectionContentSha256, changedHash.CollectionContentSha256);
    }

    [Fact]
    public void Select_omits_excluded_and_reparse_point_inputs()
    {
        var modRoot = CreateModTree("reparse");
        var outsideRoot = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outsideRoot, "Escape.cs"), "public class Escape {}");
        var linkPath = Path.Combine(modRoot, "linked.cs");
        File.CreateSymbolicLink(linkPath, Path.Combine(outsideRoot, "Escape.cs"));

        var mod = CreateMod(modRoot);

        var selected = new ReferenceModFileSelector().Select([mod]);

        Assert.DoesNotContain(selected, file => file.RelativePath.Contains("bin/", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, file => file.RelativePath.Contains("obj/", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, file => file.RelativePath.Contains("BepInEx/cache/", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, file => string.Equals(file.RelativePath, "linked.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HashAsync_rejects_input_drift_without_returning_partial_records()
    {
        var modRoot = Directory.CreateDirectory(Path.Combine(_root, "drift")).FullName;
        var filePath = Path.Combine(modRoot, "README.md");
        await File.WriteAllTextAsync(filePath, "before", TestContext.Current.CancellationToken);

        var input = new ReferenceModInputFile(
            "chem-plant",
            filePath,
            "README.md",
            ReferenceModInputKind.TextDocument,
            "Readme");

        var hasher = new ReferenceModInputHasher(path =>
        {
            File.WriteAllText(path, "after");
            return SHA256.HashData(Encoding.UTF8.GetBytes("before"));
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            hasher.HashAsync([input], TestContext.Current.CancellationToken));

        Assert.Contains("README.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashAsync_honors_cancellation()
    {
        var modRoot = Directory.CreateDirectory(Path.Combine(_root, "cancel")).FullName;
        var filePath = Path.Combine(modRoot, "README.md");
        await File.WriteAllTextAsync(filePath, "before", TestContext.Current.CancellationToken);

        var input = new ReferenceModInputFile(
            "chem-plant",
            filePath,
            "README.md",
            ReferenceModInputKind.TextDocument,
            "Readme");
        using var cancellation = new CancellationTokenSource();
        var hasher = new ReferenceModInputHasher(path =>
        {
            cancellation.Cancel();
            return SHA256.HashData(File.ReadAllBytes(path));
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            hasher.HashAsync([input], cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateModTree(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "Docs"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "cache"));

        File.WriteAllBytes(Path.Combine(root, "plugins", "ChemPlant.dll"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(root, "src", "Feature.cs"), "namespace Demo; public sealed class Feature {}");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Readme");
        File.WriteAllText(Path.Combine(root, "Docs", "Guide.txt"), "guide");
        File.WriteAllText(Path.Combine(root, "bin", "Ignored.cs"), "ignored");
        File.WriteAllText(Path.Combine(root, "obj", "Ignored.cs"), "ignored");
        File.WriteAllText(Path.Combine(root, "BepInEx", "cache", "Ignored.txt"), "ignored");

        return root;
    }

    private static ReferenceModDefinition CreateMod(
        string rootPath,
        string version = "local",
        string license = "MIT",
        string displayName = "Chemical Plant") =>
        new(
            "chem-plant",
            displayName,
            version,
            license,
            rootPath,
            string.Empty,
            ["**/*.txt", "**/*.dll", "**/*.cs", "**/*.md"],
            ["**/obj/**", "**/BepInEx/cache/**", "**/bin/**"]);
}
