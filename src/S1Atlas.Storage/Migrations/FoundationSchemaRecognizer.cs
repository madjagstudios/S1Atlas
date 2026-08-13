using System.Data;
using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Migrations;

internal static class FoundationSchemaRecognizer
{
    private static readonly string[] ExpectedTableNames =
    [
        "atlas_state",
        "builds",
        "dependencies",
        "environment_snapshots"
    ];

    private static readonly string[] ExpectedExplicitIndexNames =
    [
        "ix_dependencies_snapshot_kind",
        "ix_environment_snapshots_build_id"
    ];

    private static readonly IReadOnlyDictionary<string, ColumnDefinition[]> ExpectedColumns =
        new Dictionary<string, ColumnDefinition[]>(StringComparer.Ordinal)
        {
            ["atlas_state"] =
            [
                new("singleton_id", "INTEGER", true, 1),
                new("current_snapshot_id", "TEXT", false, 0)
            ],
            ["builds"] =
            [
                new("build_id", "TEXT", true, 1),
                new("game_version", "TEXT", false, 0),
                new("steam_build_id", "TEXT", false, 0),
                new("game_assembly_sha256", "TEXT", true, 0),
                new("metadata_sha256", "TEXT", true, 0),
                new("scanned_at_utc", "TEXT", true, 0),
                new("is_valid", "INTEGER", true, 0)
            ],
            ["dependencies"] =
            [
                new("snapshot_id", "TEXT", true, 1),
                new("ordinal", "INTEGER", true, 2),
                new("kind", "TEXT", true, 0),
                new("version", "TEXT", false, 0),
                new("path", "TEXT", false, 0),
                new("is_installed", "INTEGER", true, 0)
            ],
            ["environment_snapshots"] =
            [
                new("snapshot_id", "TEXT", true, 1),
                new("build_id", "TEXT", true, 0),
                new("atlas_version", "TEXT", true, 0),
                new("captured_at_utc", "TEXT", true, 0)
            ]
        };

    private static readonly IReadOnlyDictionary<string, ForeignKeyDefinition[]> ExpectedForeignKeys =
        new Dictionary<string, ForeignKeyDefinition[]>(StringComparer.Ordinal)
        {
            ["atlas_state"] =
            [
                new(
                    "current_snapshot_id",
                    "environment_snapshots",
                    "snapshot_id",
                    "NO ACTION",
                    "NO ACTION",
                    "NONE")
            ],
            ["builds"] = [],
            ["dependencies"] =
            [
                new(
                    "snapshot_id",
                    "environment_snapshots",
                    "snapshot_id",
                    "NO ACTION",
                    "CASCADE",
                    "NONE")
            ],
            ["environment_snapshots"] =
            [
                new(
                    "build_id",
                    "builds",
                    "build_id",
                    "NO ACTION",
                    "NO ACTION",
                    "NONE")
            ]
        };

    private static readonly IndexDefinition[] ExpectedIndexes =
    [
        new(
            "dependencies",
            "ix_dependencies_snapshot_kind",
            IsUnique: false,
            IsPartial: false,
            Origin: "c",
            Columns: ["snapshot_id", "kind"]),
        new(
            "environment_snapshots",
            "ix_environment_snapshots_build_id",
            IsUnique: false,
            IsPartial: false,
            Origin: "c",
            Columns: ["build_id"])
    ];

    public static async Task<bool> IsExactFoundationV1Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Foundation schema recognition requires an open SQLite connection.");
        }

        var tableNames = await ReadNamesAsync(
            connection,
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND substr(name, 1, 7) <> 'sqlite_'
            ORDER BY name COLLATE BINARY;
            """,
            cancellationToken);
        if (!tableNames.SequenceEqual(ExpectedTableNames, StringComparer.Ordinal))
        {
            return false;
        }

        var explicitIndexNames = await ReadNamesAsync(
            connection,
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND sql IS NOT NULL
            ORDER BY name COLLATE BINARY;
            """,
            cancellationToken);
        if (!explicitIndexNames.SequenceEqual(
                ExpectedExplicitIndexNames,
                StringComparer.Ordinal))
        {
            return false;
        }

        foreach (var tableName in ExpectedTableNames)
        {
            var actualColumns = await ReadColumnsAsync(
                connection,
                tableName,
                cancellationToken);
            if (!actualColumns.SequenceEqual(ExpectedColumns[tableName]))
            {
                return false;
            }

            var actualForeignKeys = await ReadForeignKeysAsync(
                connection,
                tableName,
                cancellationToken);
            if (!actualForeignKeys.SequenceEqual(ExpectedForeignKeys[tableName]))
            {
                return false;
            }
        }

        foreach (var expectedIndex in ExpectedIndexes)
        {
            var actualIndex = await ReadIndexAsync(
                connection,
                expectedIndex.TableName,
                expectedIndex.Name,
                cancellationToken);
            if (actualIndex is null ||
                actualIndex.IsUnique != expectedIndex.IsUnique ||
                actualIndex.IsPartial != expectedIndex.IsPartial ||
                !string.Equals(
                    actualIndex.Origin,
                    expectedIndex.Origin,
                    StringComparison.Ordinal) ||
                !actualIndex.Columns.SequenceEqual(
                    expectedIndex.Columns,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string[]> ReadNamesAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<ColumnDefinition[]> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqlLiteral(tableName)});";

        var columns = new List<ColumnDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnDefinition(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                checked((int)reader.GetInt64(5))));
        }

        return columns.ToArray();
    }

    private static async Task<ForeignKeyDefinition[]> ReadForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({QuoteSqlLiteral(tableName)});";

        var foreignKeys = new List<ForeignKeyDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreignKeys.Add(new ForeignKeyDefinition(
                reader.GetString(3),
                reader.GetString(2),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return foreignKeys
            .OrderBy(item => item.FromColumn, StringComparer.Ordinal)
            .ThenBy(item => item.TargetTable, StringComparer.Ordinal)
            .ThenBy(item => item.TargetColumn, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IndexDefinition?> ReadIndexAsync(
        SqliteConnection connection,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        bool? isUnique = null;
        bool? isPartial = null;
        string? origin = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteSqlLiteral(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(
                        reader.GetString(1),
                        indexName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                isUnique = reader.GetInt64(2) != 0;
                origin = reader.GetString(3);
                isPartial = reader.GetInt64(4) != 0;
                break;
            }
        }

        if (isUnique is null || isPartial is null || origin is null)
        {
            return null;
        }

        var columns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_info({QuoteSqlLiteral(indexName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
            }
        }

        return new IndexDefinition(
            tableName,
            indexName,
            isUnique.Value,
            isPartial.Value,
            origin,
            columns);
    }

    private static string QuoteSqlLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private sealed record ColumnDefinition(
        string Name,
        string DeclaredType,
        bool IsNotNull,
        int PrimaryKeyOrdinal);

    private sealed record ForeignKeyDefinition(
        string FromColumn,
        string TargetTable,
        string TargetColumn,
        string OnUpdate,
        string OnDelete,
        string Match);

    private sealed record IndexDefinition(
        string TableName,
        string Name,
        bool IsUnique,
        bool IsPartial,
        string Origin,
        IReadOnlyList<string> Columns);
}
