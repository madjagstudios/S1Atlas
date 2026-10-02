using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Workflow;
using Xunit;

namespace S1Atlas.IntegrationTests.GoldenFacts;

[Trait("Category", "LocalGameRequired")]
public sealed class GeneratedLocalGameTests
{
    [Fact]
    public async Task Generated_members_exist_and_naming_maps_them_to_declaring_methods()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(requireFactsFile: false, cancellationToken);

        var snapshot = await context.Repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.SkipUnless(snapshot is not null, "No current environment snapshot. Skipping: NEEDS_CONTEXT.");
        var verifier = ValidatedExtractionIntegrityVerifier.Create(new Sha256FileHasher(), context.Repository);
        var workflow = new IndexingWorkflow(
            context.Copy.DataRoot,
            context.Repository,
            (buildId, token) => new PreferredVerifiedExtractionResolver(context.Copy.DataRoot, context.Repository, verifier)
                .ResolveAsync(buildId, token),
            new ScheduleOneIndexSource(new IlSpyManagedDecompiler()),
            context.Repository);
        _ = await workflow.RunScheduleOneAsync(snapshot!.Build.BuildId, force: false, cancellationToken);

        var run = await context.Repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, null, cancellationToken);
        Assert.SkipUnless(run is not null, "No completed Schedule I Installed index. Skipping: NEEDS_CONTEXT.");

        await using var connection = await OpenReadOnlyAsync(context.Copy.DatabasePath, cancellationToken);
        var generated = await CountGeneratedAsync(connection, run!.IndexId, cancellationToken);
        Assert.True(generated > 0, $"Expected compiler-generated symbols in the real build; found {generated}.");

        // Reconstructed game assemblies carry metadata but no IL bodies, so stored
        // credited edges cannot occur here; crediting is covered by the fixture
        // suite. The live check stays referential: no dangling credit pointers.
        var orphans = await CountOrphanCreditsAsync(connection, run.IndexId, cancellationToken);
        Assert.Equal(0, orphans);

        var symbols = await LoadMethodSymbolsAsync(connection, run.IndexId, cancellationToken);
        var mappings = GeneratedBodyResolver.MapSymbols(symbols, CodebaseKind.ScheduleI, CodeChannel.Installed);
        var mapped = mappings.Values.Count(mapping => mapping.DeclaringKey is not null);
        Assert.True(
            mapped > 0,
            $"Expected the naming resolver to map generated members in the real build; mapped {mapped} of {symbols.Count} methods.");
        Assert.Contains(
            mappings.Values,
            mapping => mapping.Detail.Contains("state machine", StringComparison.Ordinal));
    }

    private static async Task<long> CountGeneratedAsync(
        SqliteConnection connection, string indexId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM symbols AS symbol " +
            "INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id " +
            "WHERE run.index_id = $id AND run.status = 'Completed' AND symbol.is_generated = 1;";
        command.Parameters.AddWithValue("$id", indexId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountOrphanCreditsAsync(
        SqliteConnection connection, string indexId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM relationships AS relationship " +
            "INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id " +
            "LEFT JOIN symbols AS source ON source.symbol_id = relationship.generated_source_symbol_id " +
            "WHERE run.index_id = $id AND run.status = 'Completed' " +
            "AND relationship.generated_source_symbol_id IS NOT NULL AND source.symbol_id IS NULL;";
        command.Parameters.AddWithValue("$id", indexId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<List<IndexSymbolRecord>> LoadMethodSymbolsAsync(
        SqliteConnection connection, string indexId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind, " +
            "symbol.qualified_name, symbol.signature FROM symbols AS symbol " +
            "INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id " +
            "WHERE run.index_id = $id AND run.status = 'Completed' " +
            "AND symbol.kind IN ('Method', 'Constructor');";
        command.Parameters.AddWithValue("$id", indexId);
        var symbols = new List<IndexSymbolRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            symbols.Add(new IndexSymbolRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), false));
        }

        return symbols;
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
}
