using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using S1Atlas.Application.Configuration;
using S1Atlas.Mcp;

if (args is not ["mcp", "serve", ..])
{
    await Console.Error.WriteLineAsync("Usage: S1Atlas.Mcp mcp serve");
    return 2;
}

var dataDirectory = AtlasDataPaths.FromEnvironment().RootDirectory;
var services = McpServerComposition.BuildReadOnlyServices(dataDirectory);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton(services);
builder.Services.AddSingleton(services.AuthorityResolver);
builder.Services.AddSingleton(services.IndexQueryService);
builder.Services.AddSingleton(services.BuildDiffService);
builder.Services.AddSingleton(services.SceneQueryService);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(McpToolCatalog).Assembly);

await builder.Build().RunAsync();
return 0;
