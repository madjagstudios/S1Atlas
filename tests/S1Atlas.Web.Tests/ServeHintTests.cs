using System.Net;
using System.Text.Json;
using S1Atlas.Application.Readiness;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class ServeHintTests : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-16T00:00:00Z");

    private const string BuildId = "build-serve-hint";
    private const string RecipeId = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "s1atlas-serve-hint-" + Guid.NewGuid().ToString("N"));

    public ServeHintTests()
    {
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    [Fact]
    public async Task ApiEnvironmentWithoutSnapshotNamesScan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new SqliteAtlasRepository(
            Path.Combine(_root, "atlas.db"),
            Path.Combine(_root, "backups"));
        await repository.InitializeAsync(cancellationToken);
        await using var server = await StartServerAsync(cancellationToken);

        using var response = await server.Client.GetAsync("/api/environment", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("NoCurrentBuild", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Scan, error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task ApiSearchWithoutIndexNamesIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new SqliteAtlasRepository(
            Path.Combine(_root, "atlas.db"),
            Path.Combine(_root, "backups"));
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveSnapshotAsync(
            ExtractionSeed.CreateSnapshot(BuildId, BaseTime), cancellationToken);
        await ExtractionSeed.SeedToolInstanceAsync(_root, "tool-instance-1", cancellationToken);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository,
            _root,
            BuildId,
            RecipeId,
            "tool-instance-1",
            ProfileDigest,
            PolicyDigest,
            BaseTime,
            cancellationToken);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                BuildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);
        await using var server = await StartServerAsync(cancellationToken);

        using var response = await server.Client.GetAsync("/api/search?q=Widget", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("NoCompletedIndex", error.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Index, error.GetProperty("hint").GetString());
    }

    private async Task<ServeServer> StartServerAsync(CancellationToken cancellationToken)
    {
        var host = ServeHost.Create(new ServeOptions(_root, 0));
        try
        {
            await host.StartAsync(cancellationToken);
            var client = new HttpClient { BaseAddress = host.BaseAddress };
            return new ServeServer(host, client);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private sealed class ServeServer(ServeHost host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }
}
