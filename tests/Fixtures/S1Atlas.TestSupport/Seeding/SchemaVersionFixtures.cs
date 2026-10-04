using S1Atlas.Storage.Migrations;

namespace S1Atlas.TestSupport.Seeding;

// Builds atlas databases at older schema versions so the read-only hosts'
// schema gates can be tested against a genuinely behind database. Versions
// are the committed migration versions (1.5.0 shipped v15, the last version
// without simple_name/is_generated).
public static class SchemaVersionFixtures
{
    public const int LastVersionWithoutMemberNameColumns = 15;

    public static int CurrentVersion => SqliteMigrations.All[^1].Version;

    public static async Task CreateBehindDatabaseAsync(
        string databasePath,
        string backupDirectory,
        int throughVersion,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughVersion);
        var migrations = SqliteMigrations.All
            .TakeWhile(migration => migration.Version <= throughVersion)
            .ToArray();
        if (migrations.Length == 0 || migrations[^1].Version != throughVersion)
        {
            throw new ArgumentException(
                $"No committed migration chain ends at version {throughVersion}.",
                nameof(throughVersion));
        }

        await new SqliteMigrationRunner(databasePath, backupDirectory, migrations)
            .MigrateAsync(cancellationToken);
    }

    public static async Task UpgradeToCurrentAsync(
        string databasePath,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        await new SqliteMigrationRunner(databasePath, backupDirectory)
            .MigrateAsync(cancellationToken);
    }
}
