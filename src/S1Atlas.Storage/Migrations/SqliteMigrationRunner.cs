using System.Globalization;
using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Migrations;

internal sealed class SqliteMigrationRunner
{
    private const string CreateLedgerSql = """
        CREATE TABLE schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            checksum TEXT NOT NULL,
            applied_at_utc TEXT NOT NULL
        );
        """;

    private const string LedgerInsertMarker = "/* S1ATLAS_MIGRATION_LEDGER */";
    private const string LedgerInsertSql = """
        INSERT INTO schema_migrations (
            version,
            name,
            checksum,
            applied_at_utc)
        VALUES (
            $version,
            $name,
            $checksum,
            $appliedAtUtc);
        """;

    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly TimeProvider _timeProvider;
    private readonly SqliteDatabaseBackupService _backupService;

    public SqliteMigrationRunner(
        string databasePath,
        string backupDirectory,
        TimeProvider? timeProvider = null)
        : this(
            databasePath,
            backupDirectory,
            SqliteMigrations.All,
            timeProvider)
    {
    }

    internal SqliteMigrationRunner(
        string databasePath,
        string backupDirectory,
        IReadOnlyList<SqliteMigration> migrations,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentNullException.ThrowIfNull(migrations);

        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _migrations = ValidateAndCopyMigrations(migrations);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backupService = new SqliteDatabaseBackupService(_timeProvider);
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var databaseDirectory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var userTables = await ReadUserTableNamesAsync(
            connection,
            cancellationToken);

        if (userTables.Count == 0)
        {
            await InitializeNewDatabaseAsync(connection, cancellationToken);
            return;
        }

        if (userTables.Contains("schema_migrations", StringComparer.Ordinal))
        {
            await MigrateVersionedDatabaseAsync(connection, cancellationToken);
            return;
        }

        if (await FoundationSchemaRecognizer.IsExactFoundationV1Async(
                connection,
                cancellationToken))
        {
            _backupService.CreateBackup(
                connection,
                _backupDirectory,
                _migrations[^1].Version);
            await AdoptFoundationDatabaseAsync(connection, cancellationToken);
            return;
        }

        throw new UnrecognizedAtlasSchemaException(
            "Atlas database schema is not recognized. No migration was attempted.");
    }

    private async Task InitializeNewDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < _migrations.Count; index++)
        {
            await ApplyMigrationAsync(
                connection,
                _migrations[index],
                createLedger: index == 0,
                cancellationToken);
        }
    }

    private async Task MigrateVersionedDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var appliedMigrations = await ReadAppliedMigrationsAsync(
            connection,
            cancellationToken);
        ValidateAppliedMigrations(appliedMigrations);

        if (appliedMigrations.Count == _migrations.Count)
        {
            return;
        }

        var pendingMigrations = _migrations
            .Skip(appliedMigrations.Count)
            .ToArray();
        _backupService.CreateBackup(
            connection,
            _backupDirectory,
            pendingMigrations[^1].Version);

        foreach (var migration in pendingMigrations)
        {
            await ApplyMigrationAsync(
                connection,
                migration,
                createLedger: false,
                cancellationToken);
        }
    }

    private async Task AdoptFoundationDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var activeMigration = _migrations[0];

        try
        {
            await CreateLedgerAsync(
                connection,
                transaction,
                cancellationToken);
            await InsertMigrationRowAsync(
                connection,
                transaction,
                activeMigration,
                cancellationToken);

            for (var index = 1; index < _migrations.Count; index++)
            {
                activeMigration = _migrations[index];
                if (!activeMigration.RequiresTransaction)
                {
                    ArgumentNullException.ThrowIfNull(transaction);
                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    transaction = null;
                    await ApplyMigrationWithoutOuterTransactionAsync(
                        connection,
                        activeMigration,
                        cancellationToken);
                    continue;
                }

                if (transaction is null)
                {
                    transaction =
                        (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                }

                await ExecuteMigrationSqlAsync(
                    connection,
                    transaction,
                    activeMigration,
                    cancellationToken);
                await InsertMigrationRowAsync(
                    connection,
                    transaction,
                    activeMigration,
                    cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw CreateMigrationFailure(activeMigration, exception);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SqliteMigration migration,
        bool createLedger,
        CancellationToken cancellationToken)
    {
        if (!migration.RequiresTransaction)
        {
            if (createLedger)
            {
                throw new InvalidOperationException(
                    $"Atlas database migration {migration.Version} '{migration.Name}' cannot skip the outer transaction while creating the migration ledger.");
            }

            await ApplyMigrationWithoutOuterTransactionAsync(connection, migration, cancellationToken);
            return;
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            if (createLedger)
            {
                await CreateLedgerAsync(
                    connection,
                    transaction,
                    cancellationToken);
            }

            await ExecuteMigrationSqlAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
            await InsertMigrationRowAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw CreateMigrationFailure(migration, exception);
        }
    }

    private async Task ApplyMigrationWithoutOuterTransactionAsync(
        SqliteConnection connection,
        SqliteMigration migration,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteMigrationSqlAsync(
                connection,
                transaction: null,
                migration,
                cancellationToken,
                includeLedgerInsert: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateMigrationFailure(migration, exception);
        }
    }

    private static async Task CreateLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CreateLedgerSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteMigrationSqlAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SqliteMigration migration,
        CancellationToken cancellationToken,
        bool includeLedgerInsert = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migration.Sql;
        if (includeLedgerInsert)
        {
            var expandedSql = command.CommandText.Replace(
                LedgerInsertMarker,
                LedgerInsertSql,
                StringComparison.Ordinal);
            if (string.Equals(expandedSql, command.CommandText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Atlas migration {migration.Version} '{migration.Name}' must contain the migration ledger marker when it manages its own transaction.");
            }

            command.CommandText = expandedSql;
            AddMigrationLedgerParameters(command, migration);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddMigrationLedgerParameters(
        SqliteCommand command,
        SqliteMigration migration)
    {
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$checksum", migration.Checksum);
        command.Parameters.AddWithValue(
            "$appliedAtUtc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    private async Task InsertMigrationRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteMigration migration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LedgerInsertSql;
        AddMigrationLedgerParameters(command, migration);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
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

    private void ValidateAppliedMigrations(
        IReadOnlyList<AppliedMigration> appliedMigrations)
    {
        if (appliedMigrations.Count == 0)
        {
            throw new InvalidOperationException(
                "Atlas migration ledger contains no applied migrations.");
        }

        if (appliedMigrations.Count > _migrations.Count)
        {
            throw new InvalidOperationException(
                "Atlas migration ledger contains a version unknown to this S1Atlas build.");
        }

        for (var index = 0; index < appliedMigrations.Count; index++)
        {
            var actual = appliedMigrations[index];
            var expected = _migrations[index];

            if (actual.Version != expected.Version)
            {
                throw new InvalidOperationException(
                    $"Atlas migration ledger is missing committed migration version {expected.Version}.");
            }

            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Atlas migration {actual.Version} name does not match the committed migration definition.");
            }

            if (!string.Equals(
                    actual.Checksum,
                    expected.Checksum,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Atlas migration {actual.Version} checksum does not match the committed migration definition.");
            }
        }
    }

    private static IReadOnlyList<SqliteMigration> ValidateAndCopyMigrations(
        IReadOnlyList<SqliteMigration> migrations)
    {
        if (migrations.Count == 0)
        {
            throw new ArgumentException(
                "At least one Atlas database migration is required.",
                nameof(migrations));
        }

        var copy = migrations.ToArray();
        for (var index = 0; index < copy.Length; index++)
        {
            var migration = copy[index];
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(migration.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(migration.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(migration.Sql);

            var expectedVersion = index + 1;
            if (migration.Version != expectedVersion)
            {
                throw new ArgumentException(
                    "Atlas database migrations must start at version 1 and be contiguous.",
                    nameof(migrations));
            }
        }

        return copy;
    }

    private static InvalidOperationException CreateMigrationFailure(
        SqliteMigration migration,
        Exception exception) =>
        new(
            $"Atlas database migration {migration.Version} '{migration.Name}' failed.",
            exception);

    private sealed record AppliedMigration(
        int Version,
        string Name,
        string Checksum);
}
