using System.Text.Json;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class FindPatchesTests
{
    [Fact]
    public async Task Find_patches_stdio_lists_patches_with_cursor_paging()
    {
        await using var atlas = await HarmonyPatchMcpAtlas.CreateAsync();
        var server = await McpTestServer.StartAsync(atlas.DataRoot, TestContext.Current.CancellationToken);

        var first = await server.Client.CallToolAsync(
            "find_patches",
            new Dictionary<string, object?>
            {
                ["selector"] = "Game.Widget::Run()",
                ["codebase"] = "scheduleI",
                ["limit"] = 2,
                ["scope"] = "reference",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(first.IsError ?? false);
        var firstText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(first.Content)).Text;
        using var firstDocument = JsonDocument.Parse(firstText);
        var firstRoot = firstDocument.RootElement;
        Assert.Equal("resolved", firstRoot.GetProperty("status").GetString());
        Assert.Equal(2, firstRoot.GetProperty("data").GetProperty("relationships").GetArrayLength());
        var cursor = firstRoot.GetProperty("data").GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = await server.Client.CallToolAsync(
            "find_patches",
            new Dictionary<string, object?>
            {
                ["selector"] = "Game.Widget::Run()",
                ["codebase"] = "scheduleI",
                ["limit"] = 2,
                ["scope"] = "reference",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
                ["cursor"] = cursor,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(second.IsError ?? false);
        var secondText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(second.Content)).Text;
        using var secondDocument = JsonDocument.Parse(secondText);
        var secondRoot = secondDocument.RootElement;
        Assert.Equal("resolved", secondRoot.GetProperty("status").GetString());
        var rows = secondRoot.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("harmony-fixture", row.GetProperty("source").GetProperty("referenceModId").GetString()));

        var third = await server.Client.CallToolAsync(
            "find_patches",
            new Dictionary<string, object?>
            {
                ["selector"] = "Game.Widget::Run()",
                ["codebase"] = "scheduleI",
                ["limit"] = 2,
                ["scope"] = "reference",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
                ["cursor"] = secondRoot.GetProperty("data").GetProperty("nextCursor").GetString(),
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(third.IsError ?? false);
        var thirdText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(third.Content)).Text;
        using var thirdDocument = JsonDocument.Parse(thirdText);
        var thirdRoot = thirdDocument.RootElement;
        Assert.Equal(2, thirdRoot.GetProperty("data").GetProperty("relationships").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(thirdRoot.GetProperty("data").GetProperty("nextCursor").GetString()));

        var fourth = await server.Client.CallToolAsync(
            "find_patches",
            new Dictionary<string, object?>
            {
                ["selector"] = "Game.Widget::Run()",
                ["codebase"] = "scheduleI",
                ["limit"] = 2,
                ["scope"] = "reference",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
                ["cursor"] = thirdRoot.GetProperty("data").GetProperty("nextCursor").GetString(),
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(fourth.IsError ?? false);
        var fourthText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(fourth.Content)).Text;
        using var fourthDocument = JsonDocument.Parse(fourthText);
        var fourthRoot = fourthDocument.RootElement;
        Assert.Single(fourthRoot.GetProperty("data").GetProperty("relationships").EnumerateArray());
        Assert.False(
            fourthRoot.GetProperty("data").TryGetProperty("nextCursor", out var nextCursor) &&
            nextCursor.ValueKind != JsonValueKind.Null);

        await server.DisposeAsync();
        await server.AssertNoSurvivorsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Find_patches_rejects_a_foreign_cursor()
    {
        await using var atlas = await HarmonyPatchMcpAtlas.CreateAsync();
        var server = await McpTestServer.StartAsync(atlas.DataRoot, TestContext.Current.CancellationToken);

        var result = await server.Client.CallToolAsync(
            "find_patches",
            new Dictionary<string, object?>
            {
                ["selector"] = "Game.Widget::Run()",
                ["codebase"] = "scheduleI",
                ["limit"] = 2,
                ["scope"] = "reference",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
                ["cursor"] = "bogus",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Equal("invalid_cursor", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        await server.DisposeAsync();
        await server.AssertNoSurvivorsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Investigate_seam_lists_patches_as_prior_art()
    {
        await using var atlas = await HarmonyPatchMcpAtlas.CreateAsync();
        var server = await McpTestServer.StartAsync(atlas.DataRoot, TestContext.Current.CancellationToken);

        var result = await server.Client.CallToolAsync(
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which patches change Run?",
                ["selector"] = "Game.Widget::Run()",
                ["scope"] = "all",
                ["collection"] = HarmonyPatchAtlas.CollectionId,
                ["details"] = true,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("resolved", root.GetProperty("status").GetString());
        var sections = root.GetProperty("data").GetProperty("evidenceSections");
        var patches = sections.EnumerateArray()
            .Single(section => section.GetProperty("family").GetString() == "Patches");
        Assert.Equal(7, patches.GetProperty("totalCount").GetInt32());
        var claims = root.GetProperty("data").GetProperty("claims");
        Assert.Contains(
            claims.EnumerateArray(),
            claim => claim.GetProperty("statement").GetString()!.Contains("patch", StringComparison.OrdinalIgnoreCase));

        await server.DisposeAsync();
        await server.AssertNoSurvivorsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}

internal sealed class HarmonyPatchMcpAtlas : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private HarmonyPatchMcpAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }

    public HarmonyPatchSeed Seed { get; private set; } = null!;

    public static async Task<HarmonyPatchMcpAtlas> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-harmony-mcp-" + Guid.NewGuid().ToString("N"));
        var atlas = new HarmonyPatchMcpAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(TestContext.Current.CancellationToken);
        atlas.Seed = await HarmonyPatchAtlas.SeedAsync(atlas._repository, atlas.DataRoot, TestContext.Current.CancellationToken);
        return atlas;
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
