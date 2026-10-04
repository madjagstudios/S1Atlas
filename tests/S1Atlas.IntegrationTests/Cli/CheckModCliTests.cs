using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CheckModCliTests
{
    [Fact]
    public async Task Two_build_json_reports_real_dependency_changes_and_patch_risk()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var result = atlas.Run("check-mod", atlas.Seed.ModPath, "--from", atlas.Seed.FromBuildId[..12], "--to", atlas.Seed.ToBuildId[..12], "--json");
        var root = CheckEnvelope(result, 3);
        var data = root.GetProperty("data");
        Assert.Equal(atlas.Seed.FromBuildId, data.GetProperty("fromBuildId").GetString());
        Assert.Equal(atlas.Seed.ToBuildId, data.GetProperty("toBuildId").GetString());
        Assert.Equal(atlas.Seed.FromIndexId, data.GetProperty("fromIndexId").GetString());
        Assert.Equal(atlas.Seed.ToIndexId, data.GetProperty("toIndexId").GetString());
        Assert.False(data.GetProperty("singleBuild").GetBoolean());
        var summary = data.GetProperty("summary");
        var counts = summary.GetProperty("counts");
        foreach (var name in new[] { "unchanged", "signature_changed", "moved", "removed", "unresolved", "resolved" })
        {
            Assert.True(counts.TryGetProperty(name, out var count));
            Assert.True(count.GetInt32() >= 0);
            Assert.True(summary.GetProperty("patchTargets").TryGetProperty(name, out _));
        }
        Assert.True(summary.GetProperty("breakingDependencies").GetInt32() > 0);
        Assert.True(summary.GetProperty("breakingPatchTargets").GetInt32() > 0);
        Assert.True(summary.GetProperty("externalReferencesNotChecked").GetInt32() > 0);

        var dependencies = data.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(dependencies.Length, new[] { "unchanged", "signature_changed", "moved", "removed", "unresolved", "resolved" }
            .Sum(status => counts.GetProperty(status).GetInt32()));
        AssertDependency(dependencies, "Removed", "removed", "Method");
        AssertDependency(dependencies, "Single", "signature_changed", "Method");
        AssertDependency(dependencies, "RenamedBefore", "moved", "Method");
        AssertDependency(dependencies, "Relocated", "moved", "Method");
        AssertDependency(dependencies, "BodyOnly", "unchanged", "Method");
        AssertDependency(dependencies, "Stable", "unchanged", "Method");
        AssertDependency(dependencies, "Count", "unchanged", "Field");
        var removed = Assert.Single(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("Removed", StringComparison.Ordinal) && dep.GetProperty("kind").GetString() == "Method");
        Assert.Equal("Demo.Api::Removed():System.Int32", removed.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
        var changed = Assert.Single(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("Single", StringComparison.Ordinal) && dep.GetProperty("kind").GetString() == "Method");
        Assert.Equal("Demo.Api::Single(System.Int32):System.Int32", changed.GetProperty("beforeSignature").GetString());
        Assert.Equal("Demo.Api::Single(System.Int32,System.String):System.Int32", changed.GetProperty("afterSignature").GetString());
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("RenamedBefore", StringComparison.Ordinal)
            && dep.GetProperty("candidates").EnumerateArray().Any(candidate => candidate.GetProperty("name").GetString()!.Contains("RenamedAfter", StringComparison.Ordinal)));
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("Relocated", StringComparison.Ordinal)
            && dep.GetProperty("candidates").EnumerateArray().Any(candidate => candidate.GetProperty("name").GetString()!.Contains("Demo.Destination", StringComparison.Ordinal)));
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("BodyOnly", StringComparison.Ordinal) && dep.GetProperty("bodyChanged").GetBoolean());
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("Stable", StringComparison.Ordinal) && !dep.GetProperty("bodyChanged").GetBoolean());
        Assert.Contains(dependencies, dep => dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("kind").GetString() == "direct_reference" && source.GetProperty("evidence").GetString() == "FACT"));
        Assert.Contains(dependencies, dep => dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("kind").GetString() == "harmony_patch" && source.GetProperty("patchKind").GetString() == "Postfix"));
        Assert.Contains(dependencies, dep => dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("kind").GetString() == "harmony_patch" && source.GetProperty("patchKind").GetString() == "Prefix"));
        Assert.Contains(dependencies, dep => dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("kind").GetString() == "reflection"));
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains("Removed", StringComparison.Ordinal)
            && dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("member").GetString()!.Contains("InteropCall", StringComparison.Ordinal)));
        Assert.DoesNotContain(dependencies, dep => dep.GetProperty("name").GetString()!.StartsWith("System.", StringComparison.Ordinal) || dep.GetProperty("name").GetString()!.StartsWith("UnityEngine.", StringComparison.Ordinal));
        foreach (var dependency in dependencies)
        {
            Assert.True(dependency.TryGetProperty("canonicalKey", out _));
            Assert.True(dependency.TryGetProperty("beforeSignature", out _));
            Assert.True(dependency.TryGetProperty("afterSignature", out _));
            Assert.True(dependency.TryGetProperty("reason", out _));
            Assert.Contains(dependency.GetProperty("evidence").GetString(), new[] { "FACT", "DERIVED" });
            var sources = dependency.GetProperty("sources").EnumerateArray()
                .Select(source => (source.GetProperty("kind").GetString(), source.GetProperty("patchKind").GetString(), source.GetProperty("member").GetString()))
                .ToArray();
            Assert.Equal(sources.Length, sources.Distinct().Count());
            foreach (var candidate in dependency.GetProperty("candidates").EnumerateArray())
            {
                Assert.True(candidate.TryGetProperty("canonicalKey", out _));
                Assert.True(candidate.TryGetProperty("signature", out _));
                Assert.Contains(candidate.GetProperty("evidence").GetString(), new[] { "FACT", "DERIVED" });
            }
        }
    }

    [Fact]
    public async Task Omitted_builds_choose_current_and_previous_completed_game_indexes()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var root = CheckEnvelope(atlas.Run("check-mod", atlas.Seed.ModPath, "--json"), 3);
        var data = root.GetProperty("data");
        Assert.Equal(atlas.Seed.FromBuildId, data.GetProperty("fromBuildId").GetString());
        Assert.Equal(atlas.Seed.ToBuildId, data.GetProperty("toBuildId").GetString());
        Assert.False(data.GetProperty("singleBuild").GetBoolean());
    }

    [Fact]
    public async Task One_completed_index_uses_single_build_resolution()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync(includeFrom: false);
        var root = CheckEnvelope(atlas.Run("check-mod", atlas.Seed.ModPath, "--json"), 0);
        var data = root.GetProperty("data");
        Assert.True(data.GetProperty("singleBuild").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("fromBuildId").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("fromIndexId").ValueKind);
        Assert.Equal(atlas.Seed.ToBuildId, data.GetProperty("toBuildId").GetString());
        Assert.Contains(data.GetProperty("dependencies").EnumerateArray(), dep => dep.GetProperty("status").GetString() == "resolved");
        Assert.Contains(data.GetProperty("dependencies").EnumerateArray(), dep => dep.GetProperty("status").GetString() == "unresolved");
        Assert.Contains(data.GetProperty("dependencies").EnumerateArray(), dep => dep.GetProperty("status").GetString() == "unresolved" && dep.GetProperty("reason").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task No_completed_game_index_returns_actionable_error()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync(includeFrom: false, includeTo: false);
        var root = CheckEnvelope(atlas.Run("check-mod", atlas.Seed.ModPath, "--json"), 1);
        Assert.Equal("NoCompletedIndex", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("s1atlas index", root.GetProperty("error").GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ambiguous_from_prefix_reports_matching_builds()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var other = await atlas.SeedAmbiguousFromAsync();
        var root = CheckEnvelope(atlas.Run("check-mod", atlas.Seed.ModPath, "--from", atlas.Seed.FromBuildId[..12], "--to", atlas.Seed.ToBuildId, "--json"), 1);
        var message = root.GetProperty("error").GetProperty("message").GetString();
        Assert.Equal("AmbiguousBuildPrefix", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("matches 2 builds", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(atlas.Seed.FromBuildId[..12], message, StringComparison.Ordinal);
        Assert.Contains(other[..12], message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_managed_input_is_a_clear_error()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var path = Path.Combine(atlas.DataRoot, "not-managed.dll");
        await File.WriteAllTextAsync(path, "not a managed assembly", TestContext.Current.CancellationToken);
        var root = CheckEnvelope(atlas.Run("check-mod", path, "--json"), 1);
        Assert.Contains("managed", root.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analysis_preserves_mod_bytes_and_atlas_database()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var modBefore = SHA256.HashData(await File.ReadAllBytesAsync(atlas.Seed.ModPath, TestContext.Current.CancellationToken));
        var databasePath = Path.Combine(atlas.DataRoot, "atlas.db");
        var dbBefore = SHA256.HashData(await File.ReadAllBytesAsync(databasePath, TestContext.Current.CancellationToken));
        CheckEnvelope(atlas.Run("check-mod", atlas.Seed.ModPath, "--from", atlas.Seed.FromBuildId, "--to", atlas.Seed.ToBuildId, "--json"), 3);
        Assert.Equal(modBefore, SHA256.HashData(await File.ReadAllBytesAsync(atlas.Seed.ModPath, TestContext.Current.CancellationToken)));
        Assert.Equal(dbBefore, SHA256.HashData(await File.ReadAllBytesAsync(databasePath, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Human_report_orders_breakage_first_and_warns_about_patches()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var result = atlas.Run("check-mod", atlas.Seed.ModPath, "--from", atlas.Seed.FromBuildId, "--to", atlas.Seed.ToBuildId);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var report = result.StandardOutput;
        Assert.Contains("removed", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signature", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("patch", report, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.IndexOf("removed", StringComparison.OrdinalIgnoreCase) < report.LastIndexOf("unchanged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancelled_analysis_returns_cancellation_envelope()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = CheckEnvelope(atlas.RunWithCancellation(cancellation.Token, "check-mod", atlas.Seed.ModPath, "--json"), 2);
        Assert.Contains("cancel", root.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDependency(JsonElement[] dependencies, string name, string status, string kind)
    {
        Assert.Contains(dependencies, dep => dep.GetProperty("name").GetString()!.Contains(name, StringComparison.Ordinal)
            && dep.GetProperty("status").GetString() == status
            && dep.GetProperty("kind").GetString() == kind);
    }

    private static JsonElement CheckEnvelope((int ExitCode, string StandardOutput, string StandardError) result, int expectedExit)
    {
        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("check-mod", root.GetProperty("command").GetString());
        Assert.Equal(expectedExit, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(expectedExit == 0, root.GetProperty("success").GetBoolean());
        if (expectedExit is 0 or 3)
            Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        else
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        return root.Clone();
    }
}

internal sealed class CheckModCliAtlas : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private CheckModCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"));
    }

    public string DataRoot { get; }
    public ModCheckSeed Seed { get; private set; } = null!;

    public static async Task<CheckModCliAtlas> CreateAsync(bool includeFrom = true, bool includeTo = true, ModCheckRegressionVariant regressionVariant = ModCheckRegressionVariant.None)
    {
        var atlas = new CheckModCliAtlas(Path.Combine(Path.GetTempPath(), "s1atlas-check-mod-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(TestContext.Current.CancellationToken);
        atlas.Seed = await ModCheckAtlas.SeedAsync(atlas._repository, atlas.DataRoot, TestContext.Current.CancellationToken, includeFrom, includeTo, regressionVariant);
        return atlas;
    }

    public (int ExitCode, string StandardOutput, string StandardError) Run(params string[] args) =>
        RunWithCancellation(TestContext.Current.CancellationToken, args);

    public (int ExitCode, string StandardOutput, string StandardError) RunWithCancellation(CancellationToken cancellationToken, params string[] args)
    {
        var application = new CliApplication(DataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, cancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    public Task<string> SeedAmbiguousFromAsync() => ModCheckAtlas.SeedAmbiguousFromAsync(_repository, DataRoot, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await TestDirectory.DeleteTreeAsync(_root);
}
