using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests;

public sealed class SqliteAtlasSchemaInspectorTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "s1atlas-schema-inspector-" + Guid.NewGuid().ToString("N"));

    public SqliteAtlasSchemaInspectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    [Fact]
    public async Task MissingDatabase_ReturnsNotCreated()
    {
        var inspector = new SqliteAtlasSchemaInspector(DatabasePath("missing.db"));

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.NotCreated, status.Kind);
        Assert.Null(status.AppliedVersion);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.ExpectedVersion);
    }

    [Fact]
    public async Task EmptyFile_ReturnsNotCreated()
    {
        var path = DatabasePath("empty.db");
        await File.WriteAllBytesAsync(path, [], CancellationToken.None);
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.NotCreated, status.Kind);
    }

    [Fact]
    public async Task MigratedDatabase_ReturnsCurrent()
    {
        var path = DatabasePath("current.db");
        var repository = new SqliteAtlasRepository(path, Path.Combine(_root, "backups"));
        await repository.InitializeAsync(CancellationToken.None);
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Current, status.Kind);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.AppliedVersion);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.ExpectedVersion);
    }

    [Fact]
    public async Task PartialLedger_ReturnsBehind()
    {
        var path = DatabasePath("behind.db");
        var runner = new SqliteMigrationRunner(
            path,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(3).ToArray());
        await runner.MigrateAsync(CancellationToken.None);
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Behind, status.Kind);
        Assert.Equal(3, status.AppliedVersion);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.ExpectedVersion);
    }

    [Fact]
    public async Task FoundationDatabaseWithoutLedger_ReturnsBehindAtVersionOne()
    {
        var path = DatabasePath("foundation.db");
        var runner = new SqliteMigrationRunner(
            path,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(1).ToArray());
        await runner.MigrateAsync(CancellationToken.None);
        await ExecuteAsync(path, "DROP TABLE schema_migrations;");
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Behind, status.Kind);
        Assert.Equal(1, status.AppliedVersion);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.ExpectedVersion);
    }

    [Fact]
    public async Task UnknownTables_ReturnsUnrecognized()
    {
        var path = DatabasePath("unknown.db");
        await ExecuteAsync(path, "CREATE TABLE something_else (id INTEGER NOT NULL PRIMARY KEY);");
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Unrecognized, status.Kind);
    }

    [Fact]
    public async Task GarbageFile_ReturnsUnrecognized()
    {
        var path = DatabasePath("garbage.db");
        await File.WriteAllTextAsync(path, "not a sqlite database", CancellationToken.None);
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Unrecognized, status.Kind);
    }

    [Fact]
    public async Task NewerLedger_ReturnsAhead()
    {
        var path = DatabasePath("ahead.db");
        var repository = new SqliteAtlasRepository(path, Path.Combine(_root, "backups"));
        await repository.InitializeAsync(CancellationToken.None);
        var next = SqliteMigrations.All[^1].Version + 1;
        await ExecuteAsync(
            path,
            "INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)" +
            $" VALUES ({next}, 'future-test', 'checksum', '2026-09-01T00:00:00Z');");
        var inspector = new SqliteAtlasSchemaInspector(path);

        var status = await inspector.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AtlasSchemaStatusKind.Ahead, status.Kind);
        Assert.Equal(next, status.AppliedVersion);
        Assert.Equal(SqliteMigrations.All[^1].Version, status.ExpectedVersion);
    }

    [Fact]
    public async Task Inspector_DoesNotCreateOrModifyTheDatabase()
    {
        var missingPath = DatabasePath("untouched.db");
        var inspector = new SqliteAtlasSchemaInspector(missingPath);

        await inspector.GetStatusAsync(CancellationToken.None);

        Assert.False(File.Exists(missingPath));
    }

    private string DatabasePath(string fileName) => Path.Combine(_root, fileName);

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
