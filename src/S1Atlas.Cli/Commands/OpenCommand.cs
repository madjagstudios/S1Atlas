using System.CommandLine;
using System.Text.Json;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Web;

namespace S1Atlas.Cli.Commands;

internal static class OpenCommand
{
    private static readonly TimeSpan StatusProbeTimeout = TimeSpan.FromSeconds(1);

    public static Command Create(
        IndexQueryService service,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        IBrowserLauncher launcher,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var selectorArgument = new Argument<string>("selector") { Description = IndexQueryCommandFactory.QueryArgumentDescription };
        var portOption = new Option<int>("--port")
        {
            Description = "The loopback port serve is listening on. 1 to 65535.",
            DefaultValueFactory = _ => ServeOptions.DefaultPort
        };
        var command = new Command("open", CliExamples.With("Open one resolved symbol in the local serve web app.", "s1atlas open \"Demo.Widget\""));
        command.Arguments.Add(selectorArgument);
        command.Options.Add(portOption);
        command.Validators.Add(result =>
        {
            try
            {
                var port = CliValidation.GetValue(result, portOption);
                if (port is < 1 or > 65535)
                    throw new CliValidationException("open", "InvalidPort", $"Invalid port '{port}'. Use 1 to 65535.");
            }
            catch (InvalidOperationException)
            {
                // A framework binding failure; the framework reports it.
            }
        });
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("open", false, output, error);
            return CommandExecution.Run(
                () => Execute(
                    service,
                    authorityResolver,
                    repository,
                    launcher,
                    parseResult.GetValue(selectorArgument)!,
                    parseResult.GetValue(portOption),
                    commandOutput,
                    output,
                    error,
                    cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        IndexQueryService service,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        IBrowserLauncher launcher,
        string selector,
        int port,
        CommandOutput commandOutput,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        var authority = authorityResolver.ResolveAsync(null, cancellationToken).GetAwaiter().GetResult();
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return commandOutput.Failure(
                1,
                authority.Status.ToString(),
                authority.Message ?? "The requested Schedule I build is unavailable.",
                hint: authority.Hint);
        }

        var resolution = service.ResolveInIndexAsync(
            authority.IndexRun!,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            cancellationToken).GetAwaiter().GetResult();
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
        {
            return IndexQueryCommandFactory.Complete(
                commandOutput,
                new IndexQueryOutput([], [], [], Resolution: resolution),
                selector,
                ReadinessFixCommands.Index);
        }

        var url = $"http://127.0.0.1:{port}/symbol/{Uri.EscapeDataString(resolution.Symbol.SymbolId)}";
        var status = ProbeStatus(port, cancellationToken);
        if (!status.Reachable)
        {
            output.WriteLine(url);
            output.WriteLine("The server is not running there; start it with `s1atlas serve`, then re-run this command.");
            return 0;
        }

        if (!string.Equals(status.ResolvedBuildId, authority.ResolvedBuildId, StringComparison.Ordinal) ||
            !string.Equals(status.IndexId, authority.IndexId, StringComparison.Ordinal))
        {
            return commandOutput.Failure(
                1,
                "ServeMismatch",
                $"The server on port {port} serves a different build or index than the local atlas " +
                $"(server build {status.ResolvedBuildId ?? "unknown"}, index {status.IndexId ?? "unknown"}; " +
                $"local build {authority.ResolvedBuildId}, index {authority.IndexId}); not opening.");
        }

        output.WriteLine(url);
        try
        {
            launcher.Launch(new Uri(url));
        }
        catch (Exception exception)
        {
            error.WriteLine($"Could not open the browser: {exception.Message}");
        }

        return 0;
    }

    private sealed record ServeStatusProbe(bool Reachable, string? ResolvedBuildId, string? IndexId);

    private static ServeStatusProbe ProbeStatus(int port, CancellationToken cancellationToken)
    {
        // Loopback only, always direct: a configured proxy must never see
        // the request, and the probe must not follow redirects elsewhere.
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = StatusProbeTimeout };
        string body;
        try
        {
            using var response = client
                .GetAsync($"http://127.0.0.1:{port}/api/status", cancellationToken)
                .GetAwaiter()
                .GetResult();
            body = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            return new ServeStatusProbe(false, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServeStatusProbe(false, null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.GetProperty("status").GetString() != "resolved")
                return new ServeStatusProbe(true, null, null);
            var build = root.GetProperty("build");
            return new ServeStatusProbe(
                true,
                build.GetProperty("resolvedBuildId").GetString(),
                build.GetProperty("indexId").GetString());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ServeStatusProbe(true, null, null);
        }
    }
}
