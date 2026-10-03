using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class PatchedByCliTests
{
    [Fact]
    public async Task Patched_by_json_lists_resolved_patches_with_provenance()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "patched-by",
            "Game.Widget::Run()",
            "--scope",
            "reference",
            "--collection",
            HarmonyPatchAtlas.CollectionId,
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("patched-by", root.GetProperty("command").GetString());
        var relationships = root.GetProperty("data").GetProperty("relationships");
        var resolved = relationships.EnumerateArray()
            .Where(edge => edge.GetProperty("target").GetProperty("resolved").GetBoolean())
            .ToArray();
        Assert.Equal(8, resolved.Length);
        Assert.All(resolved, edge =>
        {
            Assert.Equal("harmony-fixture", edge.GetProperty("source").GetProperty("referenceModId").GetString());
            Assert.Equal("harmony", edge.GetProperty("source").GetProperty("collection").GetString());
            Assert.Equal("Prefix", edge.GetProperty("generatedDetail").GetString());
        });
        var methods = resolved.Select(edge => edge.GetProperty("source").GetProperty("signature").GetString()).ToArray();
        Assert.Contains(methods, method => method!.Contains("RunPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("ManualPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("ManualEmptyTypesPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("ManualMethodInfoPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("InteropPrefixPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("StringNamePatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("ManualFloatConstantPatch", StringComparison.Ordinal));
        Assert.Contains(methods, method => method!.Contains("ManualOverloadDisambiguationPatch", StringComparison.Ordinal));
        var evidence = resolved.Select(edge => edge.GetProperty("label").GetString()).ToArray();
        Assert.Equal(3, evidence.Count(label => label == "attribute"));
        Assert.Equal(5, evidence.Count(label => label == "DERIVED"));
    }

    [Fact]
    public async Task Patched_by_human_output_lists_patches()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "patched-by",
            "Game.Widget::Run()",
            "--scope",
            "reference",
            "--collection",
            HarmonyPatchAtlas.CollectionId);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("Prefix", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("harmony-fixture", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RunPatch", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("DERIVED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("attribute", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_includes_unresolved_patches_that_name_the_method()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "patched-by",
            "Game.Widget::Compute(System.Int32,System.String)",
            "--scope",
            "reference",
            "--collection",
            HarmonyPatchAtlas.CollectionId,
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var relationships = document.RootElement.GetProperty("data").GetProperty("relationships");
        Assert.Equal(2, relationships.EnumerateArray().Count(edge => edge.GetProperty("target").GetProperty("resolved").GetBoolean()));
        var unresolved = relationships.EnumerateArray()
            .Where(edge => !edge.GetProperty("target").GetProperty("resolved").GetBoolean())
            .ToArray();
        Assert.Equal(2, unresolved.Length);
        var ambiguous = Assert.Single(unresolved, edge => edge.GetProperty("generatedDetail").GetString() == "Finalizer");
        Assert.Contains("ambiguous-overload", ambiguous.GetProperty("target").GetProperty("rawText").GetString(), StringComparison.Ordinal);
        var noOverload = Assert.Single(unresolved, edge => edge.GetProperty("generatedDetail").GetString() == "Prefix");
        Assert.Contains("no-matching-overload", noOverload.GetProperty("target").GetProperty("rawText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patched_by_game_scope_reports_the_reference_notice()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "patched-by",
            "Game.Widget::Run()",
            "--scope",
            "game",
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("patched-by", root.GetProperty("command").GetString());
        Assert.Empty(root.GetProperty("data").GetProperty("relationships").EnumerateArray());
        Assert.Contains(
            "--scope reference",
            root.GetProperty("data").GetProperty("completenessNotice").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Investigate_seam_lists_patches_as_prior_art()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "investigate-seam",
            "Game.Widget::Run()",
            "--question",
            "Which patches change Run?",
            "--scope",
            "all",
            "--collection",
            HarmonyPatchAtlas.CollectionId,
            "--details",
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var sections = document.RootElement.GetProperty("data").GetProperty("evidenceSections");
        var patches = sections.EnumerateArray()
            .Single(section => section.GetProperty("family").GetString() == "Patches");
        Assert.Equal(9, patches.GetProperty("totalCount").GetInt32());
        var claims = document.RootElement.GetProperty("data").GetProperty("claims");
        Assert.Contains(
            claims.EnumerateArray(),
            claim => claim.GetProperty("statement").GetString()!.Contains("patch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Investigate_seam_counts_unresolved_reasons_as_prior_art()
    {
        await using var atlas = await HarmonyPatchCliAtlas.CreateAsync();

        var result = atlas.Run(
            "investigate-seam",
            "Game.Widget::Compute(System.Int32,System.String)",
            "--question",
            "Which patches change Compute?",
            "--scope",
            "all",
            "--collection",
            HarmonyPatchAtlas.CollectionId,
            "--details",
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var sections = document.RootElement.GetProperty("data").GetProperty("evidenceSections");
        var patches = sections.EnumerateArray()
            .Single(section => section.GetProperty("family").GetString() == "Patches");
        Assert.Equal(4, patches.GetProperty("totalCount").GetInt32());
        var claims = document.RootElement.GetProperty("data").GetProperty("claims");
        Assert.Contains(
            claims.EnumerateArray(),
            claim => claim.GetProperty("statement").GetString()!.Contains("patch", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class HarmonyPatchCliAtlas : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private HarmonyPatchCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }

    public HarmonyPatchSeed Seed { get; private set; } = null!;

    public static async Task<HarmonyPatchCliAtlas> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-harmony-cli-" + Guid.NewGuid().ToString("N"));
        var atlas = new HarmonyPatchCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(TestContext.Current.CancellationToken);
        atlas.Seed = await HarmonyPatchAtlas.SeedAsync(atlas._repository, atlas.DataRoot, TestContext.Current.CancellationToken);
        return atlas;
    }

    public (int ExitCode, string StandardOutput, string StandardError) Run(params string[] args)
    {
        var application = new CliApplication(DataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
