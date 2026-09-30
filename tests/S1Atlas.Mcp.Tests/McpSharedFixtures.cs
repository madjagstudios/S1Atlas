using ModelContextProtocol.Client;
using Xunit;

namespace S1Atlas.Mcp.Tests;

/// <summary>
/// Seeds one atlas and serves it over one stdio server for a whole test
/// class. Tests against the shared server must be read-only; anything that
/// mutates the atlas (or needs a different seed) keeps a private one.
/// </summary>
public abstract class SharedMcpServerFixture<TAtlas> : IAsyncLifetime
    where TAtlas : IAsyncDisposable
{
    internal TAtlas Atlas { get; private set; } = default!;

    internal McpTestServer Server { get; private set; } = null!;

    public McpClient Client => Server.Client;

    protected abstract Task<TAtlas> SeedAsync();

    protected abstract string GetDataRoot(TAtlas atlas);

    public async ValueTask InitializeAsync()
    {
        Atlas = await SeedAsync();
        Server = await McpTestServer.StartAsync(GetDataRoot(Atlas));
    }

    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        if (Atlas is not null)
        {
            await Atlas.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

public sealed class SharedHealthyServerFixture : SharedMcpServerFixture<McpTestAtlas>
{
    protected override Task<McpTestAtlas> SeedAsync() =>
        McpTestAtlas.SeedHealthyInstalledBuildAsync();

    protected override string GetDataRoot(McpTestAtlas atlas) => atlas.DataRoot;
}

public sealed class SharedScenesServerFixture : SharedMcpServerFixture<McpTestAtlas>
{
    protected override Task<McpTestAtlas> SeedAsync() =>
        McpTestAtlas.SeedHealthyInstalledBuildWithScenesAsync();

    protected override string GetDataRoot(McpTestAtlas atlas) => atlas.DataRoot;
}

public sealed class SharedOc32ServerFixture : SharedMcpServerFixture<SeamMcpTestAtlas>
{
    protected override Task<SeamMcpTestAtlas> SeedAsync() =>
        SeamMcpTestAtlas.CreateOc32Async();

    protected override string GetDataRoot(SeamMcpTestAtlas atlas) => atlas.DataRoot;
}
