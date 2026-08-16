using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Sqlite;

public sealed class ReadOnlySqliteConnectionFactory
{
    private readonly string _databasePath;

    internal string DatabasePath => _databasePath;

    public ReadOnlySqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public SqliteConnection Open()
    {
        if (!File.Exists(_databasePath))
        {
            throw new FileNotFoundException(
                "The Atlas database was not found.",
                _databasePath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
