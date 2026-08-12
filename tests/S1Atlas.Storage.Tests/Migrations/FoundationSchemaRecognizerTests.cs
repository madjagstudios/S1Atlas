using Microsoft.Data.Sqlite;
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
        var schema = FoundationV1DatabaseFixture.SchemaSql.Replace(
            "            game_version TEXT NULL,\n",
            string.Empty,
            StringComparison.Ordinal);
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            schema,
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
        var schema = FoundationV1DatabaseFixture.SchemaSql.Replace(
            "        CREATE INDEX ix_dependencies_snapshot_kind\n        ON dependencies(snapshot_id, kind);\n\n",
            string.Empty,
            StringComparison.Ordinal);
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            schema,
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
        var schema = FoundationV1DatabaseFixture.SchemaSql +
            "\nCREATE TABLE unexpected(value TEXT);";
        await using var connection = await FoundationV1DatabaseFixture.OpenAsync(
            schema,
            cancellationToken);

        var result = await FoundationSchemaRecognizer.IsExactFoundationV1Async(
            connection,
            cancellationToken);

        Assert.False(result);
    }
}
