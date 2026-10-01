using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;

namespace S1Atlas.Web.Tests;

// Seeds a synthetic atlas and serves it on an ephemeral loopback port. Each
// test gets its own atlas and server, so tests never share database state.
public sealed class ServeFixture : IAsyncDisposable
{
    private readonly SyntheticAtlas? _atlas;
    private readonly string? _ownedRoot;

    private ServeFixture(SyntheticAtlas? atlas, string? ownedRoot, ServeHost host, HttpClient client)
    {
        _atlas = atlas;
        _ownedRoot = ownedRoot;
        Host = host;
        Client = client;
    }

    public ServeHost Host { get; }

    public HttpClient Client { get; }

    public SyntheticAtlas Atlas => _atlas ?? throw new InvalidOperationException(
        "This fixture serves a bare data root with no seeded atlas.");

    public Uri BaseAddress => Host.BaseAddress;

    public static async Task<ServeFixture> CreateAsync(CancellationToken ct = default)
    {
        var atlas = await SyntheticAtlas.SeedServeFixtureAsync(ct);
        return await CreateOnRootAsync(atlas.DataRoot, atlas, null, ct);
    }

    public static async Task<ServeFixture> CreateOnEmptyRootAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-serve-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return await CreateOnRootAsync(root, null, root, ct);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default) =>
        await Client.GetAsync(path, ct);

    public async Task<string> GetStringAsync(string path, CancellationToken ct = default) =>
        await (await GetAsync(path, ct)).Content.ReadAsStringAsync(ct);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync(CancellationToken.None);
        await Host.DisposeAsync();
        if (_atlas is not null)
        {
            await _atlas.DisposeAsync();
        }

        if (_ownedRoot is not null)
        {
            await TestDirectory.DeleteTreeAsync(_ownedRoot);
        }
    }

    private static async Task<ServeFixture> CreateOnRootAsync(
        string dataRoot,
        SyntheticAtlas? atlas,
        string? ownedRoot,
        CancellationToken ct)
    {
        var host = ServeHost.Create(new ServeOptions(dataRoot, 0));
        try
        {
            await host.StartAsync(ct);
            var client = new HttpClient { BaseAddress = host.BaseAddress };
            return new ServeFixture(atlas, ownedRoot, host, client);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }
}
