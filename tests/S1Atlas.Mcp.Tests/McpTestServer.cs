using ModelContextProtocol.Client;
using System.Diagnostics;
using Xunit;

namespace S1Atlas.Mcp.Tests;

/// <summary>
/// A stdio test server whose child process is tracked from spawn to exit.
/// Every server carries a unique command-line tag, so the last-resort kill
/// on dispose (and on failed connect) can only ever target its own process
/// tree, never a sibling test's server.
/// </summary>
internal sealed class McpTestServer : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly McpClient _client;
    private readonly Dictionary<int, DateTime> _ownedStarts;
    private readonly Func<string, bool> _isOwnedCommandLine;

    private McpTestServer(
        McpClient client,
        Dictionary<int, DateTime> ownedStarts,
        Func<string, bool> isOwnedCommandLine)
    {
        _client = client;
        _ownedStarts = ownedStarts;
        _isOwnedCommandLine = isOwnedCommandLine;
    }

    public McpClient Client => _client;

    public IReadOnlyCollection<int> OwnedProcessIds => _ownedStarts.Keys;

    public static Task<McpTestServer> StartAsync(
        string dataRoot,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(
            dataRoot,
            "dotnet",
            [typeof(McpToolCatalog).Assembly.Location, "mcp", "serve"],
            tagArguments: true,
            matchOverride: null,
            connectTimeout: null,
            cancellationToken);

    public static Task<McpTestServer> StartNonRespondingAsync(
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(
            dataRoot: null,
            "cmd",
            ["/c", "ping -t 127.0.0.1 >nul"],
            tagArguments: false,
            matchOverride: static commandLine =>
                commandLine.Contains("ping", StringComparison.OrdinalIgnoreCase) &&
                commandLine.Contains("127.0.0.1", StringComparison.Ordinal),
            connectTimeout: TimeSpan.FromSeconds(5),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        KillOwnedSurvivors();
    }

    public static IReadOnlyList<int> FindMatchingProcessIds(Func<string, bool> commandLineMatches)
    {
        var matches = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    if (ProcessCommandLine.TryGetCommandLine(process.Id, out var commandLine) &&
                        commandLine is not null &&
                        commandLineMatches(commandLine))
                    {
                        matches.Add(process.Id);
                    }
                }
            }
            catch (Exception)
            {
                // Exited or inaccessible mid-enumeration; ignore.
            }
        }

        return matches;
    }

    public async Task AssertNoSurvivorsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var survivors = FindOwnedSurvivors();
            if (survivors.Count == 0)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Test server processes outlived the test: {string.Join(", ", survivors)}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task<McpTestServer> StartCoreAsync(
        string? dataRoot,
        string command,
        IReadOnlyList<string> arguments,
        bool tagArguments,
        Func<string, bool>? matchOverride,
        TimeSpan? connectTimeout,
        CancellationToken cancellationToken)
    {
        var tag = $"s1atlas-test-{Guid.NewGuid():N}";
        var finalArguments = tagArguments
            ? [.. arguments, $"--test-tag={tag}"]
            : arguments.ToArray();
        Func<string, bool> isOwned = matchOverride ??
            (commandLine => commandLine.Contains(tag, StringComparison.Ordinal));

        var before = ChildProcessIds(Path.GetFileNameWithoutExtension(command));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = finalArguments,
            EnvironmentVariables = dataRoot is null
                ? null
                : new Dictionary<string, string?> { ["S1ATLAS_HOME"] = dataRoot },
            Name = "s1atlas-mcp-test",
            ShutdownTimeout = ShutdownTimeout
        });

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (connectTimeout.HasValue)
        {
            timeoutSource.CancelAfter(connectTimeout.Value);
        }

        var connectToken = timeoutSource.Token;

        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: connectToken);
            return new McpTestServer(client, OwnedStartsSince(before, command), isOwned);
        }
        catch (Exception)
        {
            KillProcessIds(OwnedStartsSince(before, command), isOwned);
            throw;
        }
    }

    private static HashSet<int> ChildProcessIds(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName)
                .Select(process =>
                {
                    using (process)
                    {
                        return process.Id;
                    }
                })
                .ToHashSet();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static Dictionary<int, DateTime> OwnedStartsSince(
        HashSet<int> before,
        string command)
    {
        var owned = new Dictionary<int, DateTime>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(command)))
        {
            try
            {
                using (process)
                {
                    if (!before.Contains(process.Id))
                    {
                        owned[process.Id] = process.StartTime.ToUniversalTime();
                    }
                }
            }
            catch (Exception)
            {
                // Exited mid-enumeration; not ours to track.
            }
        }

        return owned;
    }

    private void KillOwnedSurvivors() => KillProcessIds(_ownedStarts, _isOwnedCommandLine);

    private IReadOnlyList<int> FindOwnedSurvivors()
    {
        var survivors = new List<int>();
        foreach (var (processId, startTime) in _ownedStarts)
        {
            if (IsOwnedAndAlive(processId, startTime, _isOwnedCommandLine))
            {
                survivors.Add(processId);
            }
        }

        return survivors;
    }

    private static void KillProcessIds(
        Dictionary<int, DateTime> candidates,
        Func<string, bool> isOwned)
    {
        foreach (var (processId, startTime) in candidates)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime() != startTime)
                {
                    continue;
                }

                if (!ProcessCommandLine.TryGetCommandLine(processId, out var commandLine) ||
                    commandLine is null ||
                    !isOwned(commandLine))
                {
                    continue;
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Exited, recycled, or inaccessible; nothing to kill.
            }
        }
    }

    private static bool IsOwnedAndAlive(int processId, DateTime startTime, Func<string, bool> isOwned)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != startTime)
            {
                return false;
            }

            return ProcessCommandLine.TryGetCommandLine(processId, out var commandLine) &&
                commandLine is not null &&
                isOwned(commandLine);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
