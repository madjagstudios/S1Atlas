using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Workflow;
using Xunit;

namespace S1Atlas.IntegrationTests.GoldenFacts;

[Trait("Category", "LocalGameRequired")]
public sealed class DispatchLocalGameTests
{
    [Fact]
    public async Task Dispatch_graph_exists_and_expanded_callers_cover_exact_callers()
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
        var overrides = await CountKindAsync(connection, run!.IndexId, "Overrides", cancellationToken);
        var implements = await CountKindAsync(connection, run.IndexId, "ImplementsMethod", cancellationToken);
        Assert.True(overrides > 0, $"Expected Overrides edges in the real build; found {overrides}.");
        Assert.True(implements > 0, $"Expected ImplementsMethod edges in the real build; found {implements}.");
        // No CallsVirtual assertion: reconstructed game assemblies carry metadata
        // but no IL bodies, so virtual-call edges cannot occur here. Managed-IL
        // expansion (including CallsVirtual) is covered by the fixture suite;
        // reference (mod) indexes with real IL exercise it against game slots.

        var subject = await QueryOverrideSourceAsync(connection, run.IndexId, cancellationToken);
        Assert.SkipUnless(subject is not null, "No override edge source found. Skipping: NEEDS_CONTEXT.");

        var service = new IndexQueryService(context.Repository);
        var expanded = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, subject!, int.MaxValue, cancellationToken);
        var exact = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, subject!, int.MaxValue, cancellationToken, exact: true);

        Assert.Equal(SymbolResolutionStatus.Resolved, expanded.Resolution.Status);
        Assert.True(
            expanded.TotalCount >= exact.TotalCount,
            $"Expanded callers ({expanded.TotalCount}) covered fewer rows than exact callers ({exact.TotalCount}).");
        var expandedIds = new HashSet<string>(
            expanded.Relationships.Select(edge => edge.RelationshipId), StringComparer.Ordinal);
        Assert.All(exact.Relationships, row => Assert.Contains(row.RelationshipId, expandedIds));
        Assert.All(
            expanded.Relationships.Where(edge => edge.IsDerived),
            row => Assert.NotEmpty(row.Routes ?? []));
    }

    private static async Task<long> CountKindAsync(
        SqliteConnection connection, string indexId, string kind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM relationships AS relationship " +
            "INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id " +
            "WHERE run.index_id = $id AND run.status = 'Completed' AND relationship.relationship_kind = $kind;";
        command.Parameters.AddWithValue("$id", indexId);
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> QueryOverrideSourceAsync(
        SqliteConnection connection, string indexId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relationship.source_symbol_id FROM relationships AS relationship " +
            "INNER JOIN index_runs AS run ON run.snapshot_id = relationship.snapshot_id " +
            "WHERE run.index_id = $id AND run.status = 'Completed' AND relationship.relationship_kind = 'Overrides' " +
            "ORDER BY relationship.source_symbol_id COLLATE BINARY LIMIT 1;";
        command.Parameters.AddWithValue("$id", indexId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
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
