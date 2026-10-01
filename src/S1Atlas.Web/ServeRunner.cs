using System.Net.Sockets;

namespace S1Atlas.Web;

public static class ServeRunner
{
    public static async Task<int> RunAsync(
        string dataRoot,
        int port,
        bool open,
        IBrowserLauncher launcher,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (port is < 0 or > 65535)
        {
            await error.WriteLineAsync($"Invalid port '{port}'. Use 0 to pick an ephemeral port, or 1 to 65535.");
            return 1;
        }

        ServeHost host;
        try
        {
            host = ServeHost.Create(new ServeOptions(dataRoot, port));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Cannot start the server: {exception.Message}");
            return 1;
        }

        await using (host)
        {
            try
            {
                await host.StartAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception exception) when (IsPortTaken(exception))
            {
                await error.WriteLineAsync(port == 0
                    ? "Cannot start the server: no ephemeral port is available."
                    : $"Cannot start the server: port {port} is already in use.");
                return 1;
            }
            catch (Exception exception)
            {
                await error.WriteLineAsync($"Cannot start the server: {exception.Message}");
                return 1;
            }

            await output.WriteLineAsync($"S1Atlas serve listening on {host.BaseAddress}");
            if (open)
            {
                try
                {
                    launcher.Launch(host.BaseAddress);
                }
                catch (Exception exception)
                {
                    await error.WriteLineAsync($"Could not open the browser: {exception.Message}");
                }
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C: fall through to a clean shutdown.
            }

            await host.StopAsync(CancellationToken.None);
            return 0;
        }
    }

    private static bool IsPortTaken(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }
}
