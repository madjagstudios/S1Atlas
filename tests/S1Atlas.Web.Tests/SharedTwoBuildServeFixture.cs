using Xunit;

namespace S1Atlas.Web.Tests;

// One seeded two-build atlas and server shared by the read-only serve
// tests. xUnit builds it once per collection run and disposes it after the
// last test; tests that mutate the atlas keep a dedicated ServeFixture.
public sealed class SharedTwoBuildServeFixture : IAsyncLifetime
{
    private ServeFixture? _serve;

    public ServeFixture Serve => _serve ?? throw new InvalidOperationException(
        "The shared serve fixture is not initialized.");

    public async Task InitializeAsync()
    {
        _serve = await ServeFixture.CreateTwoBuildAsync(TestContext.Current.CancellationToken);
    }

    public async Task DisposeAsync()
    {
        if (_serve is not null)
        {
            await _serve.DisposeAsync();
            _serve = null;
        }
    }
}

// Collection tests run sequentially against the single shared server.
[CollectionDefinition("TwoBuildServe")]
public sealed class TwoBuildServeCollection : ICollectionFixture<SharedTwoBuildServeFixture>
{
}
