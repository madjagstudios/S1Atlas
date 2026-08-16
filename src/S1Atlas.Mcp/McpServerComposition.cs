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

        return new McpReadOnlyServices(
            services.Paths.RootDirectory,
            services.Repository,
            services.AuthorityResolver,
            services.IndexQueryService,
            services.BuildDiffService,
            services.SceneQueryService);
    }
}

public sealed record McpReadOnlyServices(
    string DataRoot,
    ReadOnlySqliteAtlasRepository Repository,
    InstalledBuildAuthorityResolver AuthorityResolver,
    IndexQueryService IndexQueryService,
    BuildDiffService BuildDiffService,
    SceneQueryService SceneQueryService);
