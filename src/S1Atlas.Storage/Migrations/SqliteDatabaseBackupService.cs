using System.Globalization;
using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Migrations;

internal sealed class SqliteDatabaseBackupService
{
    private readonly TimeProvider _timeProvider;

    public SqliteDatabaseBackupService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string CreateBackup(
        SqliteConnection source,
        string backupDirectory,
        int targetSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSchemaVersion);

        if (source.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Atlas database backup requires an open SQLite connection.");
        }

        var fullBackupDirectory = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(fullBackupDirectory);
        var timestamp = _timeProvider
            .GetUtcNow()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var backupPath = Path.Combine(
            fullBackupDirectory,
            $"atlas-before-schema-{targetSchemaVersion}-{timestamp}-{suffix}.db");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        try
        {
            using var destination = new SqliteConnection(connectionString);
            destination.Open();
            source.BackupDatabase(destination);
            return backupPath;
        }
        catch
        {
            TryDeletePartialBackup(backupPath);
            throw;
        }
    }

    private static void TryDeletePartialBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
