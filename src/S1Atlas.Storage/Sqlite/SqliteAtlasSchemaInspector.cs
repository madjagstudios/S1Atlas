using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;

namespace S1Atlas.Storage.Sqlite;

/// <summary>
/// Reads the atlas database schema version through a read-only connection:
/// no migration, no file creation, no writes of any kind.
/// </summary>
public sealed class SqliteAtlasSchemaInspector : IAtlasSchemaInspector
{
    private readonly string _databasePath;

    public SqliteAtlasSchemaInspector(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    // Classifies a SQLite failure seen while inspecting. Only a file that
    // was read and is genuinely not ours (corrupt, or not a database) is
    // unrecognized; access failures (busy, locked, I/O, cannot open, no
    // permission) and anything unknown mean the database could not be read,
    // which must never advise deleting it. Extended codes carry the primary
    // code in the low byte.
    internal static AtlasSchemaStatusKind MapSqliteError(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return (exception.SqliteErrorCode & 0xFF) switch
        {
            11 => AtlasSchemaStatusKind.Unrecognized, // SQLITE_CORRUPT
            26 => AtlasSchemaStatusKind.Unrecognized, // SQLITE_NOTADB
            _ => AtlasSchemaStatusKind.Unreadable,
        };
    }

    public async Task<AtlasSchemaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var expected = SqliteMigrations.All[^1].Version;
        if (!File.Exists(_databasePath))
        {
            return new AtlasSchemaStatus(AtlasSchemaStatusKind.NotCreated, null, expected);
        }

        try
        {
            await using var connection = await OpenReadOnlyAsync(cancellationToken);
            var tables = await ReadUserTableNamesAsync(connection, cancellationToken);
            if (tables.Count == 0)
            {
                return new AtlasSchemaStatus(AtlasSchemaStatusKind.NotCreated, null, expected);
            }

            if (!tables.Contains("schema_migrations", StringComparer.Ordinal))
            {
                if (await FoundationSchemaRecognizer.IsExactFoundationV1Async(
                        connection,
                        cancellationToken))
                {
                    return new AtlasSchemaStatus(
                        AtlasSchemaStatusKind.Behind,
                        SqliteMigrations.All[0].Version,
                        expected);
                }

                return new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, expected);
            }

            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);
            if (applied.Count == 0)
            {
                return new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, expected);
            }

            if (applied.Count > SqliteMigrations.All.Count)
            {
                return new AtlasSchemaStatus(
                    AtlasSchemaStatusKind.Ahead,
                    applied[^1].Version,
                    expected);
            }

            for (var index = 0; index < applied.Count; index++)
            {
                var actual = applied[index];
                var committed = SqliteMigrations.All[index];
                if (actual.Version != committed.Version ||
                    !string.Equals(actual.Name, committed.Name, StringComparison.Ordinal) ||
                    !string.Equals(actual.Checksum, committed.Checksum, StringComparison.Ordinal))
                {
                    return new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, expected);
                }
            }

            return applied.Count == SqliteMigrations.All.Count
                ? new AtlasSchemaStatus(
                    AtlasSchemaStatusKind.Current,
                    applied[^1].Version,
                    expected)
                : new AtlasSchemaStatus(
                    AtlasSchemaStatusKind.Behind,
                    applied[^1].Version,
                    expected);
        }
        catch (SqliteException exception)
        {
            return new AtlasSchemaStatus(MapSqliteError(exception), null, expected);
        }
        catch (IOException)
        {
            return new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected);
        }
        catch (UnauthorizedAccessException)
        {
            return new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected);
        }
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadUserTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND substr(name, 1, 7) <> 'sqlite_'
            ORDER BY name COLLATE BINARY;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, name, checksum
            FROM schema_migrations
            ORDER BY version;
            """;

        var migrations = new List<AppliedMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return migrations;
    }

    private sealed record AppliedMigration(int Version, string Name, string Checksum);
}
