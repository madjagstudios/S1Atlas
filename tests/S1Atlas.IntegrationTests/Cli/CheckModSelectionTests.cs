using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CheckModSelectionTests
{
    [Fact]
    public async Task Omitted_baseline_uses_single_build_when_only_newer_completed_index_exists()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();

        var data = ResultData(atlas.Run("check-mod", atlas.Seed.ModPath, "--to", atlas.Seed.FromBuildId, "--json"));

        Assert.True(data.GetProperty("singleBuild").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("fromBuildId").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("fromIndexId").ValueKind);
        Assert.Equal(atlas.Seed.FromBuildId, data.GetProperty("toBuildId").GetString());
        Assert.Equal(atlas.Seed.FromIndexId, data.GetProperty("toIndexId").GetString());
    }

    [Fact]
    public async Task Omitted_baseline_skips_newer_completed_index_for_explicit_target()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var target = await atlas.SeedAmbiguousFromAsync();

        var data = ResultData(atlas.Run("check-mod", atlas.Seed.ModPath, "--to", target, "--json"));

        Assert.Equal(atlas.Seed.FromBuildId, data.GetProperty("fromBuildId").GetString());
        Assert.Equal(atlas.Seed.FromIndexId, data.GetProperty("fromIndexId").GetString());
        Assert.Equal(target, data.GetProperty("toBuildId").GetString());
        Assert.False(data.GetProperty("singleBuild").GetBoolean());
    }

    [Fact]
    public async Task Ahead_schema_requests_upgrade_without_misleading_hint()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        var expected = SchemaVersionFixtures.CurrentVersion;
        await using (var connection = OpenDatabase(atlas))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES ($version, 'future-test', 'checksum', '2026-10-03T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$version", expected + 1);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var error = Error(atlas.Run("check-mod", atlas.Seed.ModPath, "--json"));

        Assert.Equal($"Upgrade S1Atlas to a build that understands atlas schema v{expected + 1} (this build expects v{expected}).",
            error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("hint", out _));
    }

    [Fact]
    public async Task Exclusively_locked_database_requests_retry_and_doctor()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync();
        await using var connection = OpenDatabase(atlas);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var error = Error(atlas.Run("check-mod", atlas.Seed.ModPath, "--json"));

        Assert.Equal("The atlas database could not be read (another s1atlas command may be using it). Try again.",
            error.GetProperty("message").GetString());
        Assert.Equal("s1atlas doctor", error.GetProperty("hint").GetString());
    }

    private static SqliteConnection OpenDatabase(CheckModCliAtlas atlas) => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(atlas.DataRoot, "atlas.db"),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());

    private static JsonElement ResultData((int ExitCode, string StandardOutput, string StandardError) result)
    {
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static JsonElement Error((int ExitCode, string StandardOutput, string StandardError) result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("error").Clone();
    }
}
