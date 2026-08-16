using S1Atlas.Application.Authority;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Diff;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Mcp;

public static class McpServerComposition
{
    public static McpReadOnlyServices BuildReadOnlyServices(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var dataRoot = Path.GetFullPath(dataDirectory);
        var databasePath = Path.Combine(dataRoot, "atlas.db");
        var repository = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(databasePath));
        var integrityVerifier = CreateIntegrityVerifier(repository);
        var preferredResolver = new PreferredVerifiedExtractionResolver(
            dataRoot,
            repository,
            integrityVerifier);
        var authorityResolver = new InstalledBuildAuthorityResolver(
            preferredResolver,
            repository,
            repository,
            repository);

        return new McpReadOnlyServices(
            dataRoot,
            repository,
            authorityResolver,
            new IndexQueryService(repository, dataRoot),
            new BuildDiffService(repository),
            new SceneQueryService(repository, repository));
    }

    private static IValidatedExtractionIntegrityVerifier CreateIntegrityVerifier(
        ReadOnlySqliteAtlasRepository repository)
    {
        var extractionAssembly = typeof(Sha256FileHasher).Assembly;
        var storeType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionDocumentStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(storeType)
            ?? throw new InvalidOperationException("Could not create validated extraction document store.");
        var verifierType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionIntegrityVerifier",
            throwOnError: true)!;
        var constructor = verifierType.GetConstructor(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [storeType, typeof(IFileHasher), typeof(IValidatedExtractionRepository)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not locate validated extraction integrity verifier constructor.");

        return (IValidatedExtractionIntegrityVerifier)constructor.Invoke(
            [store, new Sha256FileHasher(), repository]);
    }
}

public sealed record McpReadOnlyServices(
    string DataRoot,
    ReadOnlySqliteAtlasRepository Repository,
    InstalledBuildAuthorityResolver AuthorityResolver,
    IndexQueryService IndexQueryService,
    BuildDiffService BuildDiffService,
    SceneQueryService SceneQueryService);
