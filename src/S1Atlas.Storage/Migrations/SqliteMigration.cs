namespace S1Atlas.Storage.Migrations;

internal sealed record SqliteMigration(
    int Version,
    string Name,
    string Sql,
    bool RequiresTransaction = true,
    bool RequiresFts5Trigram = false)
{
    public string Checksum => MigrationChecksum.Compute(Version, Name, Sql);
}
