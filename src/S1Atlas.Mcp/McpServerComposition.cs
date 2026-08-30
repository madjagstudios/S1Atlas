using S1Atlas.Application.Composition;
using S1Atlas.Application.Authority;
using S1Atlas.Indexing.Diff;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Mcp;

public static class McpServerComposition
{
    public static McpReadOnlyServices BuildReadOnlyServices(string dataDirectory)
    {
        var services = ReadOnlyAtlasComposition.BuildReadOnlyServices(dataDirectory);
        var referenceQueryService = new ReferenceModQueryService(services.Repository, services.Paths.RootDirectory);
        var federatedQueryService = new FederatedIndexQueryService(services.Repository, services.Paths.RootDirectory);
        var apiIndexQueryService = new ApiIndexQueryService(services.Repository, services.IndexQueryService);
        var seamInvestigationService = new SeamInvestigationService(
            services.IndexQueryService,
            federatedQueryService,
            referenceQueryService,
            services.Repository,
            services.Repository);

        return new McpReadOnlyServices(
            services.Paths.RootDirectory,
            services.Repository,
            services.AuthorityResolver,
            services.IndexQueryService,
            apiIndexQueryService,
            federatedQueryService,
            referenceQueryService,
            seamInvestigationService,
            services.BuildDiffService,
            services.SceneQueryService);
    }
}

public sealed record McpReadOnlyServices(
    string DataRoot,
    ReadOnlySqliteAtlasRepository Repository,
    InstalledBuildAuthorityResolver AuthorityResolver,
    IndexQueryService IndexQueryService,
    ApiIndexQueryService ApiIndexQueryService,
    FederatedIndexQueryService FederatedIndexQueryService,
    ReferenceModQueryService ReferenceModQueryService,
    SeamInvestigationService SeamInvestigationService,
    BuildDiffService BuildDiffService,
    SceneQueryService SceneQueryService);
