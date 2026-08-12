namespace S1Atlas.Storage.Migrations;

internal sealed record SqliteMigration(int Version, string Name, string Sql)
{
    public string Checksum => MigrationChecksum.Compute(Version, Name, Sql);
}
