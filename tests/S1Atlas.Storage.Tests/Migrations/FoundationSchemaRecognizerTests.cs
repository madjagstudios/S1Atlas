using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class FoundationSchemaRecognizerTests
{
    [Fact]
    public void MigrationChecksum_WithSameDefinition_IsDeterministic()
    {
        var first = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");
        var second = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");

        Assert.Equal(first, second);
    }

    [Fact]
    public void MigrationChecksum_WhenSqlChanges_ChangesDigest()
    {
        var first = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");
        var second = MigrationChecksum.Compute(1, "foundation", "SELECT 2;");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task IsExactFoundationV1Async_WithShippedSchema_ReturnsTrue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            FoundationV1DatabaseFixture.SchemaSql,
            cancellationToken);

        var result = await FoundationSchemaRecognizer.IsExactFoundationV1Async(
            connection,
            cancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task IsExactFoundationV1Async_WhenColumnIsMissing_ReturnsFalse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            FoundationV1DatabaseFixture.SchemaSql,
            cancellationToken);
        await ExecuteAsync(
            connection,
            "ALTER TABLE builds DROP COLUMN game_version;",
            cancellationToken);

        var result = await FoundationSchemaRecognizer.IsExactFoundationV1Async(
            connection,
            cancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task IsExactFoundationV1Async_WhenExplicitIndexIsMissing_ReturnsFalse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            FoundationV1DatabaseFixture.SchemaSql,
            cancellationToken);
        await ExecuteAsync(
            connection,
            "DROP INDEX ix_dependencies_snapshot_kind;",
            cancellationToken);

        var result = await FoundationSchemaRecognizer.IsExactFoundationV1Async(
            connection,
            cancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task IsExactFoundationV1Async_WhenUnexpectedUserTableExists_ReturnsFalse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            FoundationV1DatabaseFixture.SchemaSql,
            cancellationToken);
        await ExecuteAsync(
            connection,
            "CREATE TABLE unexpected(value TEXT);",
            cancellationToken);

        var result = await FoundationSchemaRecognizer.IsExactFoundationV1Async(
            connection,
            cancellationToken);

        Assert.False(result);
    }

    private static async Task ExecuteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
