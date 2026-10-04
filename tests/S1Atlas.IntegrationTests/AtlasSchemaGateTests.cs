using S1Atlas.Application.Readiness;
using S1Atlas.Cli;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class AtlasSchemaGateTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-schema-gate-" + Guid.NewGuid().ToString("N"));

    public AtlasSchemaGateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Describe_CoversEveryStatusWithItsFixCommand()
    {
        var expected = SchemaVersionFixtures.CurrentVersion;

        Assert.Equal(
            ("No atlas database yet; the first scan creates it.", "s1atlas scan"),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.NotCreated, null, expected)));
        Assert.Equal(
            ("Atlas database schema is current.", null),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.Current, expected, expected)));
        Assert.Equal(
            ($"Atlas database schema v15 is older than v{expected}.", "s1atlas status"),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 15, expected)));
        Assert.Equal(
            ($"Upgrade S1Atlas to a build that understands atlas schema v{expected + 1} (this build expects v{expected}).", null),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.Ahead, expected + 1, expected)));
        Assert.Equal(
            ("Back up and remove the unrecognized atlas database, then run 's1atlas scan'.", null),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, expected)));
        Assert.Equal(
            ("The atlas database could not be read (another s1atlas command may be using it). Try again.", "s1atlas doctor"),
            SchemaStatusWording.Describe(new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected)));
    }

    [Fact]
    public void StartupLine_AppendsTheFixCommandWhenThereIsOne()
    {
        var expected = SchemaVersionFixtures.CurrentVersion;

        Assert.Equal(
            "Atlas database schema is current.",
            SchemaStatusWording.StartupLine(new AtlasSchemaStatus(AtlasSchemaStatusKind.Current, expected, expected)));
        Assert.Equal(
            $"Atlas database schema v15 is older than v{expected}. Run 's1atlas status' to fix.",
            SchemaStatusWording.StartupLine(new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 15, expected)));
        Assert.Equal(
            "The atlas database could not be read (another s1atlas command may be using it). Try again. Run 's1atlas doctor' to fix.",
            SchemaStatusWording.StartupLine(new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected)));
    }

    [Fact]
    public void BlockFor_BlocksOnlyBehindAheadUnrecognizedAndUnreadable()
    {
        var expected = SchemaVersionFixtures.CurrentVersion;

        Assert.Null(SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Current, expected, expected)));
        Assert.Null(SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.NotCreated, null, expected)));

        var behind = SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 15, expected));
        Assert.NotNull(behind);
        Assert.Equal("AtlasSchemaBehind", behind.Code);
        Assert.Equal("s1atlas status", behind.Hint);

        var ahead = SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Ahead, expected + 1, expected));
        Assert.NotNull(ahead);
        Assert.Equal("AtlasSchemaAhead", ahead.Code);
        Assert.Null(ahead.Hint);

        var unrecognized = SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Unrecognized, null, expected));
        Assert.NotNull(unrecognized);
        Assert.Equal("AtlasSchemaUnrecognized", unrecognized.Code);
        Assert.Null(unrecognized.Hint);

        var unreadable = SchemaStatusWording.BlockFor(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected));
        Assert.NotNull(unreadable);
        Assert.Equal("AtlasSchemaUnreadable", unreadable.Code);
        Assert.Equal("s1atlas doctor", unreadable.Hint);
    }

    [Fact]
    public async Task GetStatus_CachesCurrentButRechecksOtherStatuses()
    {
        var expected = SchemaVersionFixtures.CurrentVersion;
        var current = new AtlasSchemaStatus(AtlasSchemaStatusKind.Current, expected, expected);
        var inspector = new ScriptedInspector(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 15, expected),
            current,
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Behind, 15, expected));
        var gate = new AtlasSchemaGate(inspector);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(AtlasSchemaStatusKind.Behind, (await gate.GetStatusAsync(cancellationToken)).Kind);
        Assert.Equal(AtlasSchemaStatusKind.Current, (await gate.GetStatusAsync(cancellationToken)).Kind);
        Assert.Equal(AtlasSchemaStatusKind.Current, (await gate.GetStatusAsync(cancellationToken)).Kind);

        Assert.Equal(2, inspector.Calls);
    }

    [Fact]
    public async Task GetStatus_RechecksUnreadable()
    {
        var expected = SchemaVersionFixtures.CurrentVersion;
        var inspector = new ScriptedInspector(
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Unreadable, null, expected),
            new AtlasSchemaStatus(AtlasSchemaStatusKind.Current, expected, expected));
        var gate = new AtlasSchemaGate(inspector);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(AtlasSchemaStatusKind.Unreadable, (await gate.GetStatusAsync(cancellationToken)).Kind);
        Assert.Equal(AtlasSchemaStatusKind.Current, (await gate.GetStatusAsync(cancellationToken)).Kind);

        Assert.Equal(2, inspector.Calls);
    }

    [Fact]
    public async Task BehindDatabase_ReportsBehindUntilItIsUpgraded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(_root, "atlas.db");
        await SchemaVersionFixtures.CreateBehindDatabaseAsync(
            databasePath,
            Path.Combine(_root, "backups"),
            SchemaVersionFixtures.LastVersionWithoutMemberNameColumns,
            cancellationToken);

        var gate = new AtlasSchemaGate(new SqliteAtlasSchemaInspector(databasePath));
        var expected = SchemaVersionFixtures.CurrentVersion;

        var behind = await gate.GetStatusAsync(cancellationToken);
        Assert.Equal(AtlasSchemaStatusKind.Behind, behind.Kind);
        Assert.Equal(15, behind.AppliedVersion);
        Assert.Equal(expected, behind.ExpectedVersion);

        await SchemaVersionFixtures.UpgradeToCurrentAsync(
            databasePath,
            Path.Combine(_root, "backups-upgrade"),
            cancellationToken);

        var current = await gate.GetStatusAsync(cancellationToken);
        Assert.Equal(AtlasSchemaStatusKind.Current, current.Kind);
    }

    [Fact]
    public async Task StatusCommand_UpgradesABehindDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(_root, "atlas.db");
        await SchemaVersionFixtures.CreateBehindDatabaseAsync(
            databasePath,
            Path.Combine(_root, "backups"),
            SchemaVersionFixtures.LastVersionWithoutMemberNameColumns,
            cancellationToken);

        var gate = new AtlasSchemaGate(new SqliteAtlasSchemaInspector(databasePath));
        Assert.Equal(
            AtlasSchemaStatusKind.Behind,
            (await gate.GetStatusAsync(cancellationToken)).Kind);

        var application = new CliApplication(_root, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(["status"], output, error, cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            AtlasSchemaStatusKind.Current,
            (await gate.GetStatusAsync(cancellationToken)).Kind);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed class ScriptedInspector(params AtlasSchemaStatus[] statuses) : IAtlasSchemaInspector
    {
        private int _index;

        public int Calls { get; private set; }

        public Task<AtlasSchemaStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(statuses[Math.Min(_index++, statuses.Length - 1)]);
        }
    }
}
