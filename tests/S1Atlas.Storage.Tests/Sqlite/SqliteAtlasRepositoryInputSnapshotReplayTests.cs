using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryInputSnapshotReplayTests : IAsyncDisposable
{
    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.Parse("2026-08-13T02:30:00.0000000+00:00");
    private static readonly string ManifestDigest = new('f', 64);

    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryInputSnapshotReplayTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-snapshot-replay-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task GetInputSnapshotAsync_ReturnsVerifiedAndUnverifiedRowsWithFullManifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var unverified = CreateSnapshot("snapshot-unverified", "build-a", replayVerified: false);
        var verified = CreateSnapshot("snapshot-verified", "build-a", replayVerified: true);
        await repository.SaveInputSnapshotAsync(unverified, cancellationToken);
        await repository.SaveInputSnapshotAsync(verified, cancellationToken);

        var readUnverified = await repository.GetInputSnapshotAsync(
            "snapshot-unverified",
            cancellationToken);
        var readVerified = await repository.GetInputSnapshotAsync(
            "snapshot-verified",
            cancellationToken);

        AssertSnapshotEqual(unverified, readUnverified);
        AssertSnapshotEqual(verified, readVerified);
    }

    [Fact]
    public async Task GetInputSnapshotAsync_UnknownId_ReturnsNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);

        Assert.Null(await repository.GetInputSnapshotAsync("missing", cancellationToken));
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_ChangesOnlyReplayColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");

        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a",
            "build-a",
            ManifestDigest,
            verifiedAt,
            cancellationToken);

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.ReplayVerified);
        Assert.Equal(verifiedAt, reloaded.ReplayVerifiedAtUtc);
        // Every immutable fact is preserved.
        Assert.Equal(snapshot.BuildId, reloaded.BuildId);
        Assert.Equal(snapshot.RootPath, reloaded.RootPath);
        Assert.Equal(snapshot.ManifestDigest, reloaded.ManifestDigest);
        Assert.Equal(snapshot.CreatedAtUtc, reloaded.CreatedAtUtc);
        AssertManifestEqual(snapshot, reloaded);
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_RepeatedCall_PreservesFirstTimestamp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var first = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");
        var second = DateTimeOffset.Parse("2026-08-13T06:00:00.0000000+00:00");

        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a", "build-a", ManifestDigest, first, cancellationToken);
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a", "build-a", ManifestDigest, second, cancellationToken);

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.True(reloaded!.ReplayVerified);
        Assert.Equal(first, reloaded.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_BuildIdMismatch_RejectsWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.MarkInputSnapshotReplayVerifiedAsync(
                "snapshot-a", "build-other", ManifestDigest, verifiedAt, cancellationToken));

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.False(reloaded!.ReplayVerified);
        Assert.Null(reloaded.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_ManifestDigestMismatch_RejectsWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.MarkInputSnapshotReplayVerifiedAsync(
                "snapshot-a", "build-a", new string('e', 64), verifiedAt, cancellationToken));

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.False(reloaded!.ReplayVerified);
        Assert.Null(reloaded.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_UnknownSnapshot_RejectsWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.MarkInputSnapshotReplayVerifiedAsync(
                "missing", "build-a", ManifestDigest, verifiedAt, cancellationToken));

        Assert.Equal(0L, await CountAsync("input_snapshots", cancellationToken));
    }

    [Fact]
    public async Task MarkInputSnapshotReplayVerifiedAsync_Cancellation_RollsBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repository.MarkInputSnapshotReplayVerifiedAsync(
                "snapshot-a", "build-a", ManifestDigest, verifiedAt, canceled.Token));

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.False(reloaded!.ReplayVerified);
        Assert.Null(reloaded.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task SaveInputSnapshotAsync_SameImmutableSnapshotAfterCertification_IsNoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a", "build-a", ManifestDigest, verifiedAt, cancellationToken);

        // Recreating the identical snapshot bytes (always unverified) must not conflict.
        var recreated = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(recreated, cancellationToken);

        Assert.Equal(1L, await CountAsync("input_snapshots", cancellationToken));
        Assert.Equal(4L, await CountAsync("input_snapshot_files", cancellationToken));
    }

    [Fact]
    public async Task SaveInputSnapshotAsync_SameImmutableSnapshot_NeverDowngradesReplayCertification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a", "build-a", ManifestDigest, verifiedAt, cancellationToken);

        var recreated = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(recreated, cancellationToken);

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.True(reloaded!.ReplayVerified);
        Assert.Equal(verifiedAt, reloaded.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task SaveInputSnapshotAsync_GenuineImmutableMismatch_RejectsWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(cancellationToken);
        await SeedBuildAsync(repository, "build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a", replayVerified: false);
        await repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
        var verifiedAt = DateTimeOffset.Parse("2026-08-13T05:00:00.0000000+00:00");
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            "snapshot-a", "build-a", ManifestDigest, verifiedAt, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveInputSnapshotAsync(
                snapshot with { RootPath = "C:\\different-root" },
                cancellationToken));

        var reloaded = await repository.GetInputSnapshotAsync("snapshot-a", cancellationToken);
        Assert.Equal(snapshot.RootPath, reloaded!.RootPath);
        Assert.True(reloaded.ReplayVerified);
        Assert.Equal(verifiedAt, reloaded.ReplayVerifiedAtUtc);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static void AssertSnapshotEqual(InputSnapshot expected, InputSnapshot? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.InputSnapshotId, actual!.InputSnapshotId);
        Assert.Equal(expected.BuildId, actual.BuildId);
        Assert.Equal(expected.RootPath, actual.RootPath);
        Assert.Equal(expected.ManifestDigest, actual.ManifestDigest);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.ReplayVerified, actual.ReplayVerified);
        Assert.Equal(expected.ReplayVerifiedAtUtc, actual.ReplayVerifiedAtUtc);
        AssertManifestEqual(expected, actual);
    }

    private static void AssertManifestEqual(InputSnapshot expected, InputSnapshot actual)
    {
        var expectedEntries = expected.Manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var actualEntries = actual.Manifest.Entries.ToArray();
        Assert.Equal(expectedEntries.Length, actualEntries.Length);
        for (var index = 0; index < expectedEntries.Length; index++)
        {
            Assert.Equal(expectedEntries[index].RelativePath, actualEntries[index].RelativePath);
            Assert.Equal(expectedEntries[index].Role, actualEntries[index].Role);
            Assert.Equal(expectedEntries[index].Size, actualEntries[index].Size);
            Assert.Equal(expectedEntries[index].Sha256, actualEntries[index].Sha256);
        }
    }

    private async Task<SqliteAtlasRepository> CreateInitializedRepositoryAsync(
        CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(_databasePath, _backupDirectory);
        await repository.InitializeAsync(cancellationToken);
        return repository;
    }

    private static async Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        string buildId,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-13T01:00:00Z");
        var root = Path.GetFullPath(Path.Combine("C:\\games", buildId));
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(
                    buildId,
                    $"assembly-{buildId}",
                    $"metadata-{buildId}",
                    timestamp,
                    IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3",
                    "3164500",
                    "123",
                    root,
                    Path.Combine(root, "GameAssembly.dll"),
                    Path.Combine(root, "global-metadata.dat")),
                Dependencies: [],
                AtlasVersion: "0.2.0-test",
                CapturedAtUtc: timestamp),
            cancellationToken);
    }

    private static InputSnapshot CreateSnapshot(
        string snapshotId,
        string buildId,
        bool replayVerified)
    {
        var entries = new List<InputManifestEntry>
        {
            new("GameAssembly.dll", "gameAssembly", 10, new string('1', 64), CreatedAt),
            new("global-metadata.dat", "globalMetadata", 20, new string('2', 64), CreatedAt),
            new("Schedule I.exe", "executableSupport", 30, new string('3', 64), CreatedAt),
            new(
                "GameData/globalgamemanagers",
                "unityVersionSource",
                40,
                new string('4', 64),
                CreatedAt)
        };
        return new InputSnapshot(
            snapshotId,
            buildId,
            "C:\\snapshots\\input",
            ManifestDigest,
            CreatedAt,
            replayVerified,
            replayVerified ? CreatedAt.AddMinutes(1) : null,
            new InputManifest(entries));
    }

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
