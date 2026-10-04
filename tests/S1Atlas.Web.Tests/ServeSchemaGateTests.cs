using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Application.Readiness;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// Proves the serve schema gate: on a behind database every route
// short-circuits with the shared 503 page or envelope before any query
// runs, the database file is untouched, and upgrading recovers without
// a restart. A missing database is not blocked (see LandingTests).
public sealed class ServeSchemaGateTests
{
    private static readonly string[] HtmlRoutes =
    [
        "/",
        "/search",
        "/symbol/Demo.Widget",
        "/builds",
        "/builds/abcdef123456",
        "/environment",
        "/diff?from=a&to=b"
    ];

    private static readonly string[] ApiRoutes =
    [
        "/api/status",
        "/api/search?query=Widget",
        "/api/symbol/Demo.Widget",
        "/api/symbol/Demo.Widget/callers",
        "/api/symbol/Demo.Widget/callees",
        "/api/symbol/Demo.Widget/references",
        "/api/builds",
        "/api/builds/abcdef123456",
        "/api/environment",
        "/api/diff?from=a&to=b"
    ];

    [Fact]
    public async Task EveryRouteReturnsTheSchemaBlockOnABehindDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnBehindSchemaRootAsync(cancellationToken);

        foreach (var route in HtmlRoutes)
        {
            using var response = await fixture.GetAsync(route, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Atlas database schema v15 is older than", body);
            Assert.Contains(ReadinessFixCommands.Status, body);
        }

        foreach (var route in ApiRoutes)
        {
            using var response = await fixture.GetAsync(route, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "atlas_unavailable",
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(
                ReadinessFixCommands.Status,
                json.RootElement.GetProperty("error").GetProperty("hint").GetString());
        }
    }

    [Fact]
    public async Task GatedRequestsLeaveTheDatabaseBytesUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnBehindSchemaRootAsync(cancellationToken);
        var databasePath = Path.Combine(fixture.DataRoot, "atlas.db");
        var before = HashFile(databasePath);
        var filesBefore = Directory.GetFiles(fixture.DataRoot);

        foreach (var route in HtmlRoutes.Concat(ApiRoutes))
        {
            using var response = await fixture.GetAsync(route, cancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.Equal(before, HashFile(databasePath));
        Assert.Equal(
            filesBefore.OrderBy(file => file),
            Directory.GetFiles(fixture.DataRoot).OrderBy(file => file));
    }

    [Fact]
    public async Task UpgradingRecoversWithoutARestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnBehindSchemaRootAsync(cancellationToken);

        using (var blocked = await fixture.GetAsync("/api/status", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        }

        await SchemaVersionFixtures.UpgradeToCurrentAsync(
            Path.Combine(fixture.DataRoot, "atlas.db"),
            Path.Combine(fixture.DataRoot, "backups-upgrade"),
            cancellationToken);

        using var recovered = await fixture.GetAsync("/api/status", cancellationToken);
        var body = await recovered.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolved", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ServeRunnerLogsTheSchemaStatusAtStartup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateOnBehindSchemaRootAsync(cancellationToken);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(TimeSpan.FromSeconds(30));
        var output = new StringWriter();
        var run = ServeRunner.RunAsync(
            fixture.DataRoot, 0, false, new RecordingLauncher(), output, TextWriter.Null, runCts.Token);

        var address = await WaitForAddressAsync(output, runCts.Token);
        using var client = new HttpClient { BaseAddress = address };
        using var response = await client.GetAsync("/api/status", runCts.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        runCts.Cancel();

        Assert.Equal(0, await run);
        Assert.Contains("Atlas database schema v15 is older than", output.ToString());
        Assert.Contains($"Run '{ReadinessFixCommands.Status}' to fix.", output.ToString());
    }

    [Fact]
    public async Task GappedLedger_ReturnsTheUnrecognizedBlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateTwoBuildAsync(cancellationToken);
        await GapMigrationLedgerAsync(fixture.Atlas.DataRoot, cancellationToken);

        using var response = await fixture.GetAsync("/api/status", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("atlas_unavailable", error.GetProperty("code").GetString());
        Assert.Contains(
            "Back up and remove the unrecognized atlas database",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.False(error.TryGetProperty("hint", out _), body);
    }

    private static async Task GapMigrationLedgerAsync(string dataRoot, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataRoot, "atlas.db")};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS symbols_fts_ai;
            DROP TRIGGER IF EXISTS symbols_fts_ad;
            DROP TRIGGER IF EXISTS symbols_fts_au;
            DROP TABLE IF EXISTS symbols_fts;
            DELETE FROM schema_migrations WHERE version = 16;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task LockedDatabase_ReturnsUnreadableBlockAndRecoversWithoutRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-serve-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await SchemaVersionFixtures.UpgradeToCurrentAsync(
                Path.Combine(root, "atlas.db"), Path.Combine(root, "backups"), cancellationToken);

            // Locked before the host starts: nothing may cache a healthy read.
            using var lockHandle = new FileStream(
                Path.Combine(root, "atlas.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var host = ServeHost.Create(new ServeOptions(root, 0));
            await using (host)
            {
                await host.StartAsync(cancellationToken);
                using var client = new HttpClient { BaseAddress = host.BaseAddress };

                using (var blocked = await client.GetAsync("/api/status", cancellationToken))
                {
                    var body = await blocked.Content.ReadAsStringAsync(cancellationToken);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
                    using var json = JsonDocument.Parse(body);
                    var error = json.RootElement.GetProperty("error");
                    Assert.Equal("atlas_unavailable", error.GetProperty("code").GetString());
                    Assert.Equal(ReadinessFixCommands.Doctor, error.GetProperty("hint").GetString());
                    Assert.Contains(
                        "could not be read",
                        error.GetProperty("message").GetString(),
                        StringComparison.Ordinal);
                }

                lockHandle.Dispose();

                using var recovered = await client.GetAsync("/api/status", cancellationToken);
                Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

                await host.StopAsync(cancellationToken);
            }
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    private static async Task<Uri> WaitForAddressAsync(StringWriter output, CancellationToken ct)
    {
        const string marker = "S1Atlas serve listening on ";
        while (!ct.IsCancellationRequested)
        {
            var text = output.ToString();
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var rest = text[(index + marker.Length)..];
                var end = rest.IndexOfAny(['\r', '\n']);
                return new Uri(end < 0 ? rest : rest[..end]);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }

        throw new TimeoutException("The server never printed its listening address.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class RecordingLauncher : IBrowserLauncher
    {
        public void Launch(Uri address)
        {
        }
    }
}
