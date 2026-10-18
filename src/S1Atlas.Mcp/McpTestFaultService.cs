using Microsoft.Extensions.Hosting;

namespace S1Atlas.Mcp;

// Test-only fault hook (AT-141). When S1ATLAS_MCP_TEST_FAULT is set, the
// host runs this service, which throws at the point a closed client pipe
// (or an unexpected bug) would. Unset in normal use, so it never registers.
internal sealed class McpTestFaultService : IHostedService
{
    public const string EnvironmentVariable = "S1ATLAS_MCP_TEST_FAULT";

    private readonly string _fault;

    public McpTestFaultService(string fault)
    {
        _fault = fault;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        switch (_fault)
        {
            case "stop-io":
                throw new IOException("Simulated closed client pipe during shutdown.");
            case "stop-unexpected":
                throw new InvalidOperationException("Simulated unexpected failure during shutdown.");
            case "thread-io":
                // A background-thread write to a closed pipe, like a logger thread.
                var thread = new Thread(() => throw new IOException("Simulated closed pipe on a background thread."))
                {
                    IsBackground = true
                };
                thread.Start();
                thread.Join();
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }
}
