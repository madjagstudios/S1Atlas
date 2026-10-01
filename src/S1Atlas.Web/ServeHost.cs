using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using S1Atlas.Application.Composition;
using S1Atlas.Application.Envelope;
using S1Atlas.Web.Endpoints;
using S1Atlas.Web.Queries;

namespace S1Atlas.Web;

// A loopback-only, read-only Kestrel host for the local atlas web app.
// Created but not started by Create; StartAsync binds and records the actual
// bound addresses (which differ from the configured port when it is 0).
public sealed class ServeHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    // Published as immutable snapshots: a request can arrive while
    // StartAsync is recording the bound addresses, so the guard reads a
    // local copy instead of a list that is cleared and refilled in place.
    private volatile int[] _boundPorts;
    private volatile string[] _boundAddresses = [];

    private ServeHost(WebApplication app, int configuredPort)
    {
        _app = app;
        _boundPorts = [configuredPort];
    }

    public static ServeHost Create(ServeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, 65535);

        var services = ReadOnlyAtlasComposition.BuildReadOnlyServices(options.DataRoot);
        var queries = new ServeQueries(services);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddSingleton(queries);
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, options.Port);
            if (Socket.OSSupportsIPv6)
            {
                kestrel.Listen(IPAddress.IPv6Loopback, options.Port);
            }
        });

        var app = builder.Build();
        var host = new ServeHost(app, options.Port);
        app.Use(SecurityHeaders);
        app.Use(CatchAllAsGenericError);
        app.Use((context, next) => host.GuardHostAsync(context, next));
        app.Use(GuardMethod);
        StatusEndpoints.Map(app);
        SearchEndpoints.Map(app);
        SymbolEndpoints.Map(app);
        BuildsEndpoints.Map(app);
        EnvironmentEndpoints.Map(app);
        DiffEndpoints.Map(app);
        app.MapFallback(host.UnknownEndpointAsync);
        return host;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var boundAddresses = new List<string>();
        var boundPorts = new List<int>();
        foreach (var address in addresses ?? [])
        {
            boundAddresses.Add(address);
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                boundPorts.Add(uri.Port);
            }
        }

        _boundAddresses = [.. boundAddresses];
        _boundPorts = [.. boundPorts];
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _app.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public IReadOnlyList<string> BoundAddresses => _boundAddresses;

    public Uri BaseAddress
    {
        get
        {
            var boundAddresses = _boundAddresses;
            var address = boundAddresses.FirstOrDefault(
                candidate => candidate.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                ?? boundAddresses.FirstOrDefault()
                ?? throw new InvalidOperationException("The server has not been started.");
            return new Uri(address.EndsWith('/') ? address : address + "/");
        }
    }

    private static async Task SecurityHeaders(HttpContext context, Func<Task> next)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; " +
                "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        });
        await next();
    }

    private static async Task CatchAllAsGenericError(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The server hit an unexpected error.", context.RequestAborted);
        }
    }

    private async Task GuardHostAsync(HttpContext context, Func<Task> next)
    {
        string name;
        int? port;
        try
        {
            var host = context.Request.Host;
            if (!host.HasValue || string.IsNullOrEmpty(host.Host))
            {
                await RejectAsync(context, StatusCodes.Status400BadRequest, "Requests must carry a Host header.");
                return;
            }

            name = host.Host.Trim().Trim('[', ']');
            port = host.Port;
        }
        catch
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "The Host header could not be read.");
            return;
        }

        if (!IsLoopbackName(name))
        {
            await RejectAsync(context, 421, "This server only answers loopback hosts.");
            return;
        }

        var boundPorts = _boundPorts;
        if (port is null || !boundPorts.Contains(port.Value))
        {
            await RejectAsync(context, 421, "This server only answers its own bound port.");
            return;
        }

        await next();
    }

    private static bool IsLoopbackName(string name) =>
        name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || name.Equals("::1", StringComparison.OrdinalIgnoreCase)
        || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static async Task GuardMethod(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Only GET and HEAD are supported.", context.RequestAborted);
            return;
        }

        await next();
    }

    private async Task UnknownEndpointAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var envelope = ToolEnvelope<object>.NotFound(
                build: null,
                new ToolError("UnknownEndpoint", $"No API endpoint at '{context.Request.Path}'."));
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                envelope, ServeApiJson.Options, cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            Rendering.Html.Layout("Not found", "<h1>Not found</h1><p>Nothing lives at this address.</p>"),
            context.RequestAborted);
    }

    private static async Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
