using System.Diagnostics;
using System.Text;
using Xunit;

namespace S1Atlas.Mcp.Tests;

/// <summary>
/// Drives `S1Atlas.Mcp.dll mcp serve` over raw pipes, so a test can close
/// stdin, stdout or stderr one at a time, the way a departing client does.
/// </summary>
internal sealed class McpRawStdioProcess : IAsyncDisposable
{
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"s1atlas-disconnect-test","version":"0"}}}""";

    private const string InitializedNotification =
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;

    private McpRawStdioProcess(Process process)
    {
        _process = process;
        _stderrPump = PumpStderrAsync();
    }

    public string StandardErrorText
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public static async Task<McpRawStdioProcess> StartInitializedAsync(
        string dataRoot,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(McpToolCatalog).Assembly.Location);
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add($"--test-tag=s1atlas-test-{Guid.NewGuid():N}");
        startInfo.Environment["S1ATLAS_HOME"] = dataRoot;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("dotnet did not start the MCP server.");
        var server = new McpRawStdioProcess(process);
        try
        {
            var response = await server.RequestAsync(InitializeRequest, cancellationToken);
            Assert.Contains("\"result\"", response, StringComparison.Ordinal);
            await server.SendAsync(InitializedNotification);
            return server;
        }
        catch (Exception)
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task SendAsync(string jsonLine)
    {
        await _process.StandardInput.WriteLineAsync(jsonLine);
        await _process.StandardInput.FlushAsync();
    }

    public async Task<string> RequestAsync(string jsonLine, CancellationToken cancellationToken)
    {
        await SendAsync(jsonLine);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        return line ?? throw new InvalidOperationException("The server closed stdout before responding.");
    }

    public void CloseStdin() => _process.StandardInput.Close();

    public void CloseStdout() => _process.StandardOutput.Close();

    public void CloseStderr() => _process.StandardError.Close();

    public async Task<int> WaitForExitCodeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"The server was still running {timeout.TotalSeconds:0}s after its client went away. Stderr: {StandardErrorText}");
        }

        await _stderrPump;
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        await _process.WaitForExitAsync(CancellationToken.None);
        await _stderrPump;
        _process.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory())) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException or InvalidOperationException)
        {
            // The test closed stderr on purpose; stop collecting.
        }
    }
}
