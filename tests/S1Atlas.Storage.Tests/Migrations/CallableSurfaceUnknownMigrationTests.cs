using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class CallableSurfaceUnknownMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-callable-unknown-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public CallableSurfaceUnknownMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task UpgradeFromEighteen_PreservesKnownUnavailabilityAndPersistsUnknownAvailability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(18).ToArray())
            .MigrateAsync(cancellationToken);

        var repository = new SqliteAtlasRepository(_databasePath);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-legacy", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "source-legacy", "2026-10-10T00:00:00Z");
        var hashedSymbol = new IndexSymbolRecord(
            "symbol-hashed", snapshot.SnapshotId, "key-hashed", "Method",
            "Demo.Widget::Hashed()", "System.Void Demo.Widget::Hashed()", false);
        var missingSymbol = new IndexSymbolRecord(
            "symbol-missing", snapshot.SnapshotId, "key-missing", "Method",
            "Demo.Widget::Missing()", "System.Void Demo.Widget::Missing()", false);
        var hashedRow = new IndexCallableSurfaceRecord(
            "surface-hashed", "index-legacy", snapshot.SnapshotId, hashedSymbol.SymbolId,
            hashedSymbol.CanonicalKey, "Assembly-CSharp.dll", "interop-hash", null,
            CallableSurfaceKind.PublicMethodWrapper, false, CallableSurfaceStatus.Unavailable,
            InteropInputTrust.LocalOnly, "no matching wrapper in indexed interop assembly");
        var missingRow = new IndexCallableSurfaceRecord(
            "surface-missing", "index-legacy", snapshot.SnapshotId, missingSymbol.SymbolId,
            missingSymbol.CanonicalKey, "Assembly-CSharp.dll", null, null,
            CallableSurfaceKind.PublicMethodWrapper, false, CallableSurfaceStatus.Unavailable,
            InteropInputTrust.LocalOnly, "no usable interop wrapper");

        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(new IndexRunRecord(
            "index-legacy", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        await repository.CompleteIndexRunAsync(
            "index-legacy",
            new IndexWriteSet([hashedSymbol, missingSymbol], [], [], [], [], [hashedRow, missingRow]),
            "2026-10-10T00:01:00Z", cancellationToken);

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        var upgradedRows = await repository.GetCompletedCallableSurfaceAsync("index-legacy", cancellationToken);
        Assert.Equal(hashedRow, Assert.Single(upgradedRows, row => row.GameSymbolId == hashedSymbol.SymbolId));
        var upgradedMissingRow = Assert.Single(upgradedRows, row => row.GameSymbolId == missingSymbol.SymbolId);
        Assert.Equal(CallableSurfaceStatus.Unknown, upgradedMissingRow.Status);
        Assert.Equal(
            "interop availability is unknown because no interop assembly was indexed",
            upgradedMissingRow.Evidence);
        Assert.Equal(missingRow, upgradedMissingRow with
        {
            Status = missingRow.Status,
            Evidence = missingRow.Evidence
        });

        var newSnapshot = snapshot with { SnapshotId = "snapshot-new", SourceIdentity = "source-new" };
        var newSymbol = missingSymbol with { SymbolId = "symbol-new", SnapshotId = newSnapshot.SnapshotId };
        var unknownRow = missingRow with
        {
            CallableSurfaceId = "surface-new",
            IndexId = "index-new",
            SnapshotId = newSnapshot.SnapshotId,
            GameSymbolId = newSymbol.SymbolId,
            Status = CallableSurfaceStatus.Unknown,
            Evidence = upgradedMissingRow.Evidence
        };
        await repository.CreateCodeSnapshotAsync(newSnapshot, cancellationToken);
        await repository.StartIndexRunAsync(new IndexRunRecord(
            "index-new", newSnapshot.SnapshotId, IndexRunStatus.Running, newSnapshot.CreatedAtUtc), cancellationToken);
        await repository.CompleteIndexRunAsync(
            "index-new", new IndexWriteSet([newSymbol], [], [], [], [], [unknownRow]),
            "2026-10-10T00:02:00Z", cancellationToken);

        Assert.Equal(unknownRow, Assert.Single(
            await repository.GetCompletedCallableSurfaceAsync("index-new", cancellationToken)));
        var readOnly = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath));
        Assert.Equal(unknownRow, Assert.Single(
            await readOnly.GetCompletedCallableSurfaceAsync("index-new", cancellationToken)));
        Assert.Equal(upgradedRows,
            await readOnly.GetCompletedCallableSurfaceAsync("index-legacy", cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteTree(_root);
        return ValueTask.CompletedTask;
    }
}
