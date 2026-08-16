using Xunit;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Storage.Tests;

public sealed class ReadOnlySqliteAtlasRepositoryTests
{
    [Fact]
    public void Open_MissingDatabase_ThrowsAndCreatesNothing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var dbPath = Path.Combine(dir, "atlas.db");

        var factory = new ReadOnlySqliteConnectionFactory(dbPath);

        Assert.Throws<FileNotFoundException>(() => factory.Open());
        Assert.False(File.Exists(dbPath), "read-only open must not create the database");
    }

    [Fact]
    public async Task ReadsSeededBuilds_AndRejectsMutations()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var dbPath = Path.Combine(dir, "atlas.db");
        var backups = Directory.CreateDirectory(Path.Combine(dir, "backups")).FullName;

        var writable = new SqliteAtlasRepository(dbPath, backups);
        await writable.InitializeAsync(CancellationToken.None);
        await StorageTestData.SeedCurrentScheduleIBuildAsync(
            writable,
            CancellationToken.None);

        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(dbPath));

        var builds = await readOnly.ListBuildsAsync(CancellationToken.None);
        Assert.NotEmpty(builds);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readOnly.InitializeAsync(CancellationToken.None));
    }
}
