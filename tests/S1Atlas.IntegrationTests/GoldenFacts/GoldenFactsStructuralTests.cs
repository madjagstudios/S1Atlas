using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.IntegrationTests.GoldenFacts;

[Trait("Category", "LocalGameRequired")]
public sealed class GoldenFactsStructuralTests
{
    [Fact]
    public async Task Resolved_canonical_keys_round_trip_through_the_resolver()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: false, cancellationToken);

        var run = await context.Repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, environmentSnapshotId: null, cancellationToken);
        Assert.SkipUnless(
            run is not null,
            "No completed Schedule I Installed index exists locally. Skipping: NEEDS_CONTEXT.");

        var sample = (await context.Repository.SearchCompletedSymbolsAsync(
                run!.IndexId, "e", limit: 50, cancellationToken, kind: "Method"))
            .ToArray();
        Assert.True(
            sample.Length > 0,
            "The completed index holds no method symbols; cannot round-trip.");

        var service = new IndexQueryService(context.Repository);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI);
        var failures = new List<string>();
        foreach (var symbol in sample)
        {
            var byKey = await service.ResolveAsync(symbol.CanonicalKey, options, cancellationToken);
            if (byKey.Status != SymbolResolutionStatus.Resolved ||
                !string.Equals(byKey.Symbol?.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
            {
                failures.Add($"'{symbol.CanonicalKey}' resolved as {byKey.Status}.");
                continue;
            }

            var byId = await service.ResolveAsync(symbol.SymbolId, options, cancellationToken);
            if (byId.Status != SymbolResolutionStatus.Resolved ||
                !string.Equals(byId.Symbol?.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
                failures.Add($"'{symbol.SymbolId}' resolved as {byId.Status}.");
        }

        Assert.True(
            failures.Count == 0,
            $"Resolver round-trips failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(10))}");
    }

    [Fact]
    public async Task Stored_recovery_edge_ids_are_unique_within_each_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: false, cancellationToken);
        await using var connection = await OpenReadOnlyAsync(context.Copy.DatabasePath, cancellationToken);

        Assert.SkipUnless(
            await CountAsync(connection, "SELECT COUNT(*) FROM native_recovery_runs;", cancellationToken) > 0,
            "The copied atlas holds no native-recovery runs. Skipping: NEEDS_CONTEXT.");

        var duplicates = await QueryStringsAsync(
            connection,
            "SELECT recovery_id FROM native_recovery_edges " +
            "GROUP BY recovery_id HAVING COUNT(*) != COUNT(DISTINCT edge_id);",
            cancellationToken);
        Assert.True(
            duplicates.Count == 0,
            $"Runs with colliding edge IDs: {string.Join(", ", duplicates.Take(10))}.");
    }

    [Fact]
    public async Task Stored_recovery_edges_carry_parseable_nonzero_pointers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: false, cancellationToken);
        await using var connection = await OpenReadOnlyAsync(context.Copy.DatabasePath, cancellationToken);

        var rows = await QueryEdgesAsync(connection, cancellationToken);
        Assert.SkipUnless(
            rows.Count > 0,
            "The copied atlas holds no native-recovery edges. Skipping: NEEDS_CONTEXT.");

        var failures = new List<string>();
        foreach (var (recoveryId, ordinal, source, target, evidence, isComplete) in rows)
        {
            var where = $"{recoveryId}#{ordinal}";
            if (!TryParsePointer(source, out var sourceValue) || sourceValue == 0)
            {
                failures.Add($"{where}: unparseable source pointer '{source}'.");
                continue;
            }

            var siteIndex = evidence.LastIndexOf(" at ", StringComparison.Ordinal);
            if (siteIndex < 0 ||
                !TryParsePointer(evidence[(siteIndex + 4)..], out var siteValue) ||
                siteValue == 0)
            {
                failures.Add($"{where}: unparseable call site in '{evidence}'.");
                continue;
            }

            if (siteValue == sourceValue)
                failures.Add($"{where}: call site equals the method entry.");

            if (isComplete &&
                (!TryParsePointer(target, out var targetValue) || targetValue == 0))
                failures.Add($"{where}: complete edge without a target pointer.");
        }

        Assert.True(
            failures.Count == 0,
            $"Edge pointer checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(10))}");
    }

    [Fact]
    public async Task Stored_recovery_edge_ordinals_are_dense_from_zero()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: false, cancellationToken);
        await using var connection = await OpenReadOnlyAsync(context.Copy.DatabasePath, cancellationToken);

        Assert.SkipUnless(
            await CountAsync(connection, "SELECT COUNT(*) FROM native_recovery_runs;", cancellationToken) > 0,
            "The copied atlas holds no native-recovery runs. Skipping: NEEDS_CONTEXT.");

        var sparse = await QueryStringsAsync(
            connection,
            "SELECT recovery_id FROM native_recovery_edges GROUP BY recovery_id " +
            "HAVING MIN(ordinal) != 0 OR COUNT(*) != MAX(ordinal) + 1;",
            cancellationToken);
        Assert.True(
            sparse.Count == 0,
            $"Runs with non-dense edge ordinals: {string.Join(", ", sparse.Take(10))}.");
    }

    private static bool TryParsePointer(string? text, out ulong value)
    {
        value = 0;
        return !string.IsNullOrEmpty(text) &&
            text.StartsWith("0x", StringComparison.Ordinal) &&
            ulong.TryParse(
                text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> QueryStringsAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<IReadOnlyList<(string RecoveryId, long Ordinal, string Source, string? Target, string Evidence, bool IsComplete)>> QueryEdgesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT recovery_id, ordinal, source_method_pointer, " +
            "target_method_pointer, evidence, is_complete " +
            "FROM native_recovery_edges ORDER BY recovery_id, ordinal;";
        var rows = new List<(string, long, string, string?, string, bool)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0));
        return rows;
    }
}
