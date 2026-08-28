using System.Text.Json;
using S1Atlas.Cli;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class ReferenceModCliTests
{
    [Fact]
    public async Task Reference_manifest_validate_returns_stable_nonzero_for_invalid_manifest()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        File.WriteAllText(atlas.ManifestPath, "{ \"collection\": \"QOL\", \"mods\": [] }");

        var result = atlas.Run("reference", "collections", "validate", atlas.ManifestPath, "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("InvalidManifest", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(atlas.ModRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reference_manifest_validate_reports_counts_without_echoing_local_paths()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();

        var result = atlas.Run("reference", "collections", "validate", atlas.ManifestPath, "--json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("qol", data.GetProperty("collection").GetString());
        Assert.Equal(1, data.GetProperty("modCount").GetInt32());
        Assert.Equal(2, data.GetProperty("fileCount").GetInt32());
        Assert.DoesNotContain(atlas.ModRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(atlas.ManifestPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reference_index_reuses_and_force_rebuilds_selected_collection_offline()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();

        var first = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        var reused = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        var forced = atlas.Run("reference", "index", atlas.ManifestPath, "--force", "--json");

        Assert.True(first.ExitCode == 0, first.StandardOutput + first.StandardError);
        Assert.True(reused.ExitCode == 0, reused.StandardOutput + reused.StandardError);
        Assert.True(forced.ExitCode == 0, forced.StandardOutput + forced.StandardError);
        using var firstJson = JsonDocument.Parse(first.StandardOutput);
        using var reusedJson = JsonDocument.Parse(reused.StandardOutput);
        using var forcedJson = JsonDocument.Parse(forced.StandardOutput);
        Assert.False(firstJson.RootElement.GetProperty("data").GetProperty("reused").GetBoolean());
        Assert.True(reusedJson.RootElement.GetProperty("data").GetProperty("reused").GetBoolean());
        Assert.Equal(
            firstJson.RootElement.GetProperty("data").GetProperty("indexId").GetString(),
            reusedJson.RootElement.GetProperty("data").GetProperty("indexId").GetString());
        Assert.False(forcedJson.RootElement.GetProperty("data").GetProperty("reused").GetBoolean());
        Assert.NotEqual(
            firstJson.RootElement.GetProperty("data").GetProperty("indexId").GetString(),
            forcedJson.RootElement.GetProperty("data").GetProperty("indexId").GetString());
        Assert.Equal(0, atlas.NetworkRequestCount);
        Assert.DoesNotContain(atlas.ModRoot, first.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(atlas.ManifestPath, first.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reference_collection_list_has_human_and_json_parity_without_absolute_roots()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);

        var human = atlas.Run("reference", "collections", "list");
        var json = atlas.Run("reference", "collections", "list", "--json");

        Assert.Equal(0, human.ExitCode);
        Assert.Equal(0, json.ExitCode);
        Assert.Contains("qol", human.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Selected Mod", human.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(atlas.ModRoot, human.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(atlas.ModRoot, json.StandardOutput, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var collection = Assert.Single(document.RootElement.GetProperty("data").GetProperty("collections").EnumerateArray());
        Assert.Equal("qol", collection.GetProperty("collection").GetString());
        Assert.Equal(1, collection.GetProperty("modCount").GetInt32());
    }

    [Fact]
    public async Task Scoped_queries_select_game_reference_and_all_provenance()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        atlas.Run("reference", "index", atlas.ManifestPath, "--json");

        var game = atlas.Run("search", "Alpha", "--scope", "game", "--json");
        var reference = atlas.Run("search", "Selected", "--scope", "reference", "--collection", "qol", "--json");
        var all = atlas.Run("search", "Beta", "--scope", "all", "--collection", "qol", "--json");

        Assert.Equal(0, game.ExitCode);
        Assert.Equal(0, reference.ExitCode);
        Assert.True(all.ExitCode == 0, all.StandardOutput + all.StandardError);
        using var gameJson = JsonDocument.Parse(game.StandardOutput);
        using var referenceJson = JsonDocument.Parse(reference.StandardOutput);
        using var allJson = JsonDocument.Parse(all.StandardOutput);
        Assert.Equal("game", gameJson.RootElement.GetProperty("data").GetProperty("results")[0].GetProperty("origin").GetString());
        Assert.Equal("reference", referenceJson.RootElement.GetProperty("data").GetProperty("results")[0].GetProperty("origin").GetString());
        Assert.Equal("qol", referenceJson.RootElement.GetProperty("data").GetProperty("results")[0].GetProperty("collection").GetString());
        Assert.Equal("game", allJson.RootElement.GetProperty("data").GetProperty("results")[0].GetProperty("origin").GetString());
        Assert.DoesNotContain(atlas.ModRoot, reference.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(atlas.ManifestPath, reference.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("source")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("refs")]
    public async Task Reference_scoped_queries_require_collection_and_game_scope_rejects_one(string command)
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();

        var missing = atlas.Run(command, "Alpha", "--scope", "reference", "--json");
        var rejected = atlas.Run(command, "Alpha", "--scope", "game", "--collection", "qol", "--json");

        Assert.Equal(1, missing.ExitCode);
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("InvalidOptionCombination", missing.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("InvalidOptionCombination", rejected.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--scene")]
    [InlineData("--build", "build-current")]
    [InlineData("--codebase", "s1api")]
    [InlineData("--channel", "installed")]
    [InlineData("--commit", "commit")]
    public async Task Interop_path_rejects_non_schedule_i_combinations(params string[] combination)
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var args = new[] { "index", "--interop-path", "interop.dll" }.Concat(combination).Append("--json").ToArray();

        var result = atlas.Run(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("InvalidOptionCombination", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_and_method_keep_their_existing_game_api_contract_without_scope_options()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();

        var type = atlas.Run("type", "Alpha", "--scope", "reference", "--collection", "qol", "--json");
        var method = atlas.Run("method", "Alpha", "--scope", "reference", "--collection", "qol", "--json");

        Assert.NotEqual(0, type.ExitCode);
        Assert.NotEqual(0, method.ExitCode);
    }
}

internal sealed class ReferenceCliFixture : IAsyncDisposable
{
    private readonly CliParityAtlas _atlas;
    private readonly RejectingHandler _network;

    private ReferenceCliFixture(CliParityAtlas atlas, RejectingHandler network, string manifestPath, string modRoot)
    {
        _atlas = atlas;
        _network = network;
        ManifestPath = manifestPath;
        ModRoot = modRoot;
    }

    public string ManifestPath { get; }
    public string ModRoot { get; }
    public int NetworkRequestCount => _network.RequestCount;

    public static async Task<ReferenceCliFixture> CreateAsync()
    {
        var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();
        var modRoot = Path.Combine(atlas.DataRoot, "reference-input", "selected");
        Directory.CreateDirectory(modRoot);
        File.Copy(
            typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location,
            Path.Combine(modRoot, "Selected.dll"));
        File.WriteAllText(Path.Combine(modRoot, "README.md"), "Selected Mod README");
        var manifestPath = Path.Combine(atlas.DataRoot, "reference-input", "qol.json");
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "collection": "QOL",
              "collectionName": "Quality of Life",
              "mods": [
                {
                  "id": "selected",
                  "displayName": "Selected Mod",
                  "rootPath": "{{JsonEscape(modRoot)}}",
                  "version": "1.0.0",
                  "license": "MIT",
                  "include": ["**/*.dll", "**/*.md"]
                }
              ]
            }
            """,
            System.Text.Encoding.UTF8);
        var network = new RejectingHandler();
        return new ReferenceCliFixture(atlas, network, manifestPath, modRoot);
    }

    public (int ExitCode, string StandardOutput, string StandardError) Run(params string[] args)
    {
        var application = new CliApplication(
            _atlas.DataRoot,
            "0.1.0-test",
            Path.Combine(_atlas.DataRoot, "config"),
            () => new HttpClient(_network, disposeHandler: false));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    public async ValueTask DisposeAsync() => await _atlas.DisposeAsync();

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Reference indexing must not invoke network clients.");
        }
    }
}
