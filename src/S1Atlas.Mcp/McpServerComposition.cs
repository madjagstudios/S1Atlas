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
        var federatedQueryService = new FederatedIndexQueryService(services.IndexQueryService, referenceQueryService);

        return new McpReadOnlyServices(
            services.Paths.RootDirectory,
            services.Repository,
            services.AuthorityResolver,
            services.IndexQueryService,
            federatedQueryService,
            referenceQueryService,
            services.BuildDiffService,
            services.SceneQueryService);
    }
}

public sealed record McpReadOnlyServices(
    string DataRoot,
    ReadOnlySqliteAtlasRepository Repository,
    InstalledBuildAuthorityResolver AuthorityResolver,
    IndexQueryService IndexQueryService,
    FederatedIndexQueryService FederatedIndexQueryService,
    ReferenceModQueryService ReferenceModQueryService,
    BuildDiffService BuildDiffService,
    SceneQueryService SceneQueryService);
