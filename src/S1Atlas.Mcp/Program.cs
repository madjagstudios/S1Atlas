using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using S1Atlas.Application.Configuration;
using S1Atlas.Application.Envelope;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp;
using S1Atlas.Mcp.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
builder.Services.AddSingleton(services.ApiIndexQueryService);
builder.Services.AddSingleton(services.FederatedIndexQueryService);
builder.Services.AddSingleton(services.ReferenceModQueryService);
builder.Services.AddSingleton(services.SeamInvestigationService);
builder.Services.AddSingleton(services.BuildDiffService);
builder.Services.AddSingleton(services.SceneQueryService);
var toolJsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};
toolJsonOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver
{
    Modifiers =
    {
        typeInfo =>
        {
            foreach (var property in typeInfo.Properties)
                property.ShouldSerialize = (_, value) => value is not null;

            if (typeInfo.Type == typeof(SeamEvidenceClaim))
            {
                var evidenceClassification = typeInfo.Properties.FirstOrDefault(
                    property => property.Name == "evidenceClassification");
                if (evidenceClassification is not null)
                    typeInfo.Properties.Remove(evidenceClassification);
            }
        }
    }
};
toolJsonOptions.Converters.Insert(0, new SeamToolEnvelopeJsonConverter());
toolJsonOptions.Converters.Insert(0, new ToolStatusJsonConverter());
toolJsonOptions.Converters.Insert(0, new ProvenanceClassificationJsonConverter());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(McpToolCatalog).Assembly, toolJsonOptions);

await builder.Build().RunAsync();
return 0;
