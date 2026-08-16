using S1Atlas.Application.Authority;
using S1Atlas.Application.Configuration;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Diff;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Application.Composition;

public static class ReadOnlyAtlasComposition
{
    public static AtlasReadOnlyServices BuildReadOnlyServices(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var paths = new AtlasDataPaths(Path.GetFullPath(dataDirectory));
        var repository = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(paths.DatabasePath));
        var integrityVerifier = ValidatedExtractionIntegrityVerifier.Create(
            new Sha256FileHasher(),
            repository);
        var preferredResolver = new PreferredVerifiedExtractionResolver(
            paths.RootDirectory,
            repository,
            integrityVerifier);
        var authorityResolver = new InstalledBuildAuthorityResolver(
            preferredResolver,
            repository,
            repository,
            repository);

        return new AtlasReadOnlyServices(
            paths,
            repository,
            authorityResolver,
            new IndexQueryService(repository, paths.RootDirectory),
            new BuildDiffService(repository),
            new SceneQueryService(repository, repository));
    }
}

public sealed record AtlasReadOnlyServices(
    AtlasDataPaths Paths,
    ReadOnlySqliteAtlasRepository Repository,
    InstalledBuildAuthorityResolver AuthorityResolver,
    IndexQueryService IndexQueryService,
    BuildDiffService BuildDiffService,
    SceneQueryService SceneQueryService);
