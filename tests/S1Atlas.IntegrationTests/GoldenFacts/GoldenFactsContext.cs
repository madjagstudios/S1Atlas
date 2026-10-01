using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests.GoldenFacts;

internal sealed record GoldenFactsContext(
    LiveAtlasCopy Copy,
    SqliteAtlasRepository Repository,
    GoldenFactsFile? Facts) : IAsyncDisposable
{
    public static async Task<GoldenFactsContext> OpenAsync(
        bool requireFactsFile, CancellationToken cancellationToken)
    {
        Assert.SkipUnless(
            LiveAtlasCopy.IsExplicitlyEnabled,
            $"LocalGameRequired tests run only with {LiveAtlasCopy.EnableVariable}=1. " +
            "Skipping: NEEDS_CONTEXT.");

        GoldenFactsFile? facts = null;
        if (requireFactsFile)
        {
            var (file, localPath) = GoldenFactsFile.Load();
            Assert.SkipUnless(
                file is not null,
                $"No golden facts file at '{localPath}'. Copy golden-facts.example.json " +
                "to that path and fill in values from your own atlas. Skipping: NEEDS_CONTEXT.");
            facts = file;
        }

        var liveDataRoot = LiveAtlasCopy.ResolveLiveDataRoot();
        Assert.SkipUnless(
            File.Exists(Path.Combine(liveDataRoot, "atlas.db")),
            $"No S1Atlas database found at '{liveDataRoot}' (override with S1ATLAS_HOME). " +
            "Skipping: NEEDS_CONTEXT.");

        var copy = await LiveAtlasCopy.CreateAsync(liveDataRoot, cancellationToken);
        Assert.SkipUnless(
            copy is not null,
            "The temp drive cannot hold a copy of the live atlas. Skipping: NEEDS_CONTEXT.");
        LiveAtlasCopy.RequireTestTempRoot(copy!.DataRoot);

        var repository = new SqliteAtlasRepository(
            copy.DatabasePath, Path.Combine(copy.DataRoot, "backups"));
        await repository.InitializeAsync(cancellationToken);
        return new GoldenFactsContext(copy, repository, facts);
    }

    public async ValueTask DisposeAsync()
    {
        Repository.Dispose();
        await Copy.DisposeAsync();
    }
}
