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
        Assert.True(firstJson.RootElement.GetProperty("data").GetProperty("phases").GetProperty("inputHashMilliseconds").GetInt64() >= 0);
        var human = atlas.Run("reference", "index", atlas.ManifestPath);
        Assert.Contains("hash ", human.StandardOutput, StringComparison.Ordinal);
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

    [Fact]
    public async Task Reference_collection_name_and_index_selectors_return_identical_canonical_provenance()
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);
        using var indexedJson = JsonDocument.Parse(indexed.StandardOutput);
        var indexId = indexedJson.RootElement.GetProperty("data").GetProperty("indexId").GetString()!;

        var byName = atlas.Run("search", "Selected", "--scope", "reference", "--collection", "qol", "--json");
        var byIndex = atlas.Run("search", "Selected", "--scope", "reference", "--collection", indexId, "--json");

        Assert.True(byName.ExitCode == 0, byName.StandardOutput + byName.StandardError);
        Assert.True(byIndex.ExitCode == 0, byIndex.StandardOutput + byIndex.StandardError);
        using var nameJson = JsonDocument.Parse(byName.StandardOutput);
        using var indexJson = JsonDocument.Parse(byIndex.StandardOutput);
        var nameResult = nameJson.RootElement.GetProperty("data").GetProperty("results")[0];
        var indexResult = indexJson.RootElement.GetProperty("data").GetProperty("results")[0];
        Assert.Equal("qol", nameResult.GetProperty("collection").GetString());
        Assert.Equal("qol", indexResult.GetProperty("collection").GetString());
        Assert.Equal(nameResult.GetProperty("collection").GetString(), indexResult.GetProperty("collection").GetString());
        Assert.Equal(nameResult.GetProperty("referenceModId").GetString(), indexResult.GetProperty("referenceModId").GetString());
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("all")]
    public async Task Successful_scoped_source_and_relationship_queries_return_reference_provenance(string scope)
    {
        await using var atlas = await ReferenceCliFixture.CreateAsync();
        var indexed = atlas.Run("reference", "index", atlas.ManifestPath, "--json");
        Assert.True(indexed.ExitCode == 0, indexed.StandardOutput + indexed.StandardError);
        using var indexedJson = JsonDocument.Parse(indexed.StandardOutput);
        var indexId = indexedJson.RootElement.GetProperty("data").GetProperty("indexId").GetString()!;

        var wrapperSearch = atlas.Run("search", "InteropWrapper", "--scope", "reference", "--collection", "qol", "--json");
        var runtimeSearch = atlas.Run("search", "il2cpp_runtime_invoke", "--scope", "reference", "--collection", "qol", "--json");
        Assert.True(wrapperSearch.ExitCode == 0, wrapperSearch.StandardOutput + wrapperSearch.StandardError);
        Assert.True(runtimeSearch.ExitCode == 0, runtimeSearch.StandardOutput + runtimeSearch.StandardError);
        using var wrapperJson = JsonDocument.Parse(wrapperSearch.StandardOutput);
        using var runtimeJson = JsonDocument.Parse(runtimeSearch.StandardOutput);
        var wrapper = wrapperJson.RootElement.GetProperty("data").GetProperty("results")[0];
        var runtime = runtimeJson.RootElement.GetProperty("data").GetProperty("results")[0];
        var wrapperSelector = wrapper.GetProperty("qualifiedName").GetString()!;
        var runtimeSelector = runtime.GetProperty("qualifiedName").GetString()!;
        await atlas.AddSourceLocationAsync(indexId, wrapper.GetProperty("symbolId").GetString()!);

        var source = atlas.Run("source", wrapperSelector, "--scope", scope, "--collection", "qol", "--context", "0", "--json");
        var callers = atlas.Run("callers", runtimeSelector, "--scope", scope, "--collection", "qol", "--json");
        var callees = atlas.Run("callees", wrapperSelector, "--scope", scope, "--collection", "qol", "--json");
        var refs = atlas.Run("refs", wrapperSelector, "--scope", scope, "--collection", "qol", "--json");

        Assert.True(source.ExitCode == 0, source.StandardOutput + source.StandardError);
        Assert.True(callers.ExitCode == 0, callers.StandardOutput + callers.StandardError);
        Assert.True(callees.ExitCode == 0, callees.StandardOutput + callees.StandardError);
        Assert.True(refs.ExitCode == 0, refs.StandardOutput + refs.StandardError);

        using var sourceJson = JsonDocument.Parse(source.StandardOutput);
        var sourceData = sourceJson.RootElement.GetProperty("data");
        var sourceSymbol = sourceData.GetProperty("symbol");
        Assert.Equal("reference", sourceSymbol.GetProperty("origin").GetString());
        Assert.Equal("qol", sourceSymbol.GetProperty("collection").GetString());
        Assert.Equal("selected", sourceSymbol.GetProperty("referenceModId").GetString());
        Assert.Equal(sourceData.GetProperty("relativePath").GetString(), sourceSymbol.GetProperty("relativePath").GetString());
        Assert.Equal(sourceData.GetProperty("sha256").GetString(), sourceSymbol.GetProperty("sha256").GetString());
        Assert.False(string.IsNullOrWhiteSpace(sourceData.GetProperty("relativePath").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(sourceData.GetProperty("sha256").GetString()));

        AssertReferenceRelationship(callers.StandardOutput, expectedSource: "selected", expectedTarget: "selected");
        AssertReferenceRelationship(callees.StandardOutput, expectedSource: "selected", expectedTarget: "selected");
        AssertReferenceRelationship(refs.StandardOutput, expectedSource: "selected", expectedTarget: "selected");
    }

    private static void AssertReferenceRelationship(string output, string expectedSource, string expectedTarget)
    {
        using var json = JsonDocument.Parse(output);
        var relationship = json.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray()
            .Where(candidate => candidate.GetProperty("source").GetProperty("referenceModId").GetString() == expectedSource &&
                candidate.GetProperty("source").GetProperty("qualifiedName").GetString()?.Contains("InteropFixtureRoot::InteropWrapper", StringComparison.Ordinal) == true &&
                candidate.GetProperty("target").GetProperty("referenceModId").GetString() == expectedTarget)
            .ToArray();
        Assert.NotEmpty(relationship);
        var edge = relationship[0];
        var source = edge.GetProperty("source");
        var target = edge.GetProperty("target");
        Assert.Equal("reference", source.GetProperty("origin").GetString());
        Assert.Equal("qol", source.GetProperty("collection").GetString());
        Assert.Equal(expectedSource, source.GetProperty("referenceModId").GetString());
        Assert.Equal("reference", target.GetProperty("origin").GetString());
        Assert.Equal("qol", target.GetProperty("collection").GetString());
        Assert.Equal(expectedTarget, target.GetProperty("referenceModId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("relativePath").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("sha256").GetString()));
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

    public async Task AddSourceLocationAsync(string indexId, string symbolId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_atlas.DataRoot}/atlas.db");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_locations(symbol_id, source_file_id, start_line, start_column, end_line, end_column)
            SELECT $symbolId, source_file_id, 1, 1, 1, 1
            FROM source_files
            WHERE snapshot_id = (SELECT snapshot_id FROM index_runs WHERE index_id = $indexId)
            ORDER BY relative_path COLLATE BINARY
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$indexId", indexId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

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
