using S1Atlas.Core.Tools;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryToolTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryToolTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-tool-repository-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task SaveVerifiedManagedToolAsync_RoundTripsInstallationAndToolInstance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var installation = CreateInstallation(
            installedAtUtc: DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            lastVerifiedAtUtc: DateTimeOffset.Parse("2026-08-13T01:05:00Z"));
        var instance = CreateToolInstance(
            installation,
            observedPath: Path.Combine(installation.RootPath, "Cpp2IL.exe"),
            firstObservedAtUtc: installation.InstalledAtUtc,
            lastVerifiedAtUtc: installation.LastVerifiedAtUtc);

        await repository.SaveVerifiedManagedToolAsync(
            installation,
            instance,
            cancellationToken);

        var storedInstallation = await repository.GetManagedToolAsync(
            installation.ToolId,
            installation.Version,
            installation.Platform,
            cancellationToken);
        var storedInstance = await repository.GetToolInstanceAsync(
            instance.ToolInstanceId,
            cancellationToken);

        Assert.NotNull(storedInstallation);
        Assert.Equal(installation.ToolId, storedInstallation.ToolId);
        Assert.Equal(installation.Version, storedInstallation.Version);
        Assert.Equal(installation.Platform, storedInstallation.Platform);
        Assert.Equal(installation.DefinitionDigest, storedInstallation.DefinitionDigest);
        Assert.Equal(installation.PackageSha256, storedInstallation.PackageSha256);
        Assert.Equal(installation.ExecutableSha256, storedInstallation.ExecutableSha256);
        Assert.Equal(installation.RootPath, storedInstallation.RootPath);
        Assert.Equal(ToolInstallationStatus.Verified, storedInstallation.Status);
        Assert.Equal(installation.InstalledAtUtc, storedInstallation.InstalledAtUtc);
        Assert.Equal(installation.LastVerifiedAtUtc, storedInstallation.LastVerifiedAtUtc);
        Assert.Single(storedInstallation.ProbeResults);
        Assert.Equal("help", storedInstallation.ProbeResults[0].ProbeId);
        Assert.True(storedInstallation.ProbeResults[0].Succeeded);

        Assert.NotNull(storedInstance);
        Assert.Equal(instance.ToolInstanceId, storedInstance.ToolInstanceId);
        Assert.Equal(instance.ToolName, storedInstance.ToolName);
        Assert.Equal(instance.VersionLabel, storedInstance.VersionLabel);
        Assert.Equal(instance.Platform, storedInstance.Platform);
        Assert.Equal(ToolTrustLevel.ManagedPinned, storedInstance.TrustLevel);
        Assert.Equal(instance.DefinitionDigest, storedInstance.DefinitionDigest);
        Assert.Equal(instance.PackageSha256, storedInstance.PackageSha256);
        Assert.Equal(instance.ExecutableSha256, storedInstance.ExecutableSha256);
        Assert.Equal(instance.ObservedPath, storedInstance.ObservedPath);
        Assert.Equal(instance.FirstObservedAtUtc, storedInstance.FirstObservedAtUtc);
        Assert.Equal(instance.LastVerifiedAtUtc, storedInstance.LastVerifiedAtUtc);
        Assert.Equal(ToolInstallationStatus.Verified, storedInstance.Status);
    }

    [Fact]
    public async Task SaveVerifiedManagedToolAsync_ReverificationPreservesFirstObservedAndUpdatesLastVerified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var installation = CreateInstallation(
            installedAtUtc: DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            lastVerifiedAtUtc: DateTimeOffset.Parse("2026-08-13T01:05:00Z"));
        var originalFirstObserved = DateTimeOffset.Parse("2026-08-13T00:55:00Z");
        var first = CreateToolInstance(
            installation,
            observedPath: Path.Combine(installation.RootPath, "Cpp2IL.exe"),
            firstObservedAtUtc: originalFirstObserved,
            lastVerifiedAtUtc: installation.LastVerifiedAtUtc);
        await repository.SaveVerifiedManagedToolAsync(
            installation,
            first,
            cancellationToken);
        var laterVerification = DateTimeOffset.Parse("2026-08-13T02:00:00Z");
        var movedPath = Path.Combine(
            _temporaryDirectory,
            "moved-tools",
            "Cpp2IL.exe");
        var reverifiedInstallation = installation with
        {
            RootPath = Path.GetDirectoryName(movedPath)!,
            LastVerifiedAtUtc = laterVerification
        };
        var second = CreateToolInstance(
            reverifiedInstallation,
            movedPath,
            firstObservedAtUtc: laterVerification,
            lastVerifiedAtUtc: laterVerification);

        await repository.SaveVerifiedManagedToolAsync(
            reverifiedInstallation,
            second,
            cancellationToken);

        var stored = await repository.GetToolInstanceAsync(
            first.ToolInstanceId,
            cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(originalFirstObserved, stored.FirstObservedAtUtc);
        Assert.Equal(laterVerification, stored.LastVerifiedAtUtc);
        Assert.Equal(movedPath, stored.ObservedPath);
    }

    [Fact]
    public async Task SaveVerifiedManagedToolAsync_WhenInstallationIsNotVerified_RejectsWithoutRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var installation = CreateInstallation(
            DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-13T01:05:00Z")) with
        {
            Status = ToolInstallationStatus.Corrupt
        };
        var instance = CreateToolInstance(
            installation with { Status = ToolInstallationStatus.Verified },
            Path.Combine(installation.RootPath, "Cpp2IL.exe"),
            installation.InstalledAtUtc,
            installation.LastVerifiedAtUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveVerifiedManagedToolAsync(
                installation,
                instance,
                cancellationToken));

        Assert.Null(await repository.GetManagedToolAsync(
            installation.ToolId,
            installation.Version,
            installation.Platform,
            cancellationToken));
        Assert.Null(await repository.GetToolInstanceAsync(
            instance.ToolInstanceId,
            cancellationToken));
    }

    [Fact]
    public async Task SaveVerifiedManagedToolAsync_WhenToolInstanceIdentityDisagrees_RollsBackBothRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var installation = CreateInstallation(
            DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-13T01:05:00Z"));
        var validInstance = CreateToolInstance(
            installation,
            Path.Combine(installation.RootPath, "Cpp2IL.exe"),
            installation.InstalledAtUtc,
            installation.LastVerifiedAtUtc);
        var invalidInstance = validInstance with
        {
            ToolInstanceId = new string('f', 64)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveVerifiedManagedToolAsync(
                installation,
                invalidInstance,
                cancellationToken));

        Assert.Null(await repository.GetManagedToolAsync(
            installation.ToolId,
            installation.Version,
            installation.Platform,
            cancellationToken));
        Assert.Null(await repository.GetToolInstanceAsync(
            invalidInstance.ToolInstanceId,
            cancellationToken));
    }

    [Fact]
    public async Task SaveToolInstanceAsync_RoundTripsCustomOverrideWithNullPinnedProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var firstObserved = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
        var executableSha256 = new string('c', 64);
        var instance = new ToolInstance(
            ToolInstanceId.Create(
                "cpp2il",
                executableSha256,
                "win-x64",
                ToolTrustLevel.CustomOverride),
            "cpp2il",
            VersionLabel: null,
            "win-x64",
            ToolTrustLevel.CustomOverride,
            DefinitionDigest: null,
            PackageSha256: null,
            executableSha256,
            Path.Combine(_temporaryDirectory, "custom", "Cpp2IL.exe"),
            firstObserved,
            firstObserved,
            ToolInstallationStatus.Verified);

        await repository.SaveToolInstanceAsync(instance, cancellationToken);

        var stored = await repository.GetToolInstanceAsync(
            instance.ToolInstanceId,
            cancellationToken);
        Assert.Equal(instance, stored);
        Assert.Null(await repository.GetManagedToolAsync(
            "cpp2il",
            "test-version",
            "win-x64",
            cancellationToken));
    }

    [Fact]
    public async Task SaveToolInstanceAsync_ReverificationPreservesFirstObservedAndUpdatesProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var installation = CreateInstallation(
            DateTimeOffset.Parse("2026-08-13T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-13T01:05:00Z"));
        var originalFirstObserved = DateTimeOffset.Parse("2026-08-13T00:55:00Z");
        var first = CreateToolInstance(
            installation,
            Path.Combine(_temporaryDirectory, "first", "Cpp2IL.exe"),
            originalFirstObserved,
            installation.LastVerifiedAtUtc);
        var later = DateTimeOffset.Parse("2026-08-13T04:00:00Z");
        var second = first with
        {
            ObservedPath = Path.Combine(_temporaryDirectory, "second", "Cpp2IL.exe"),
            FirstObservedAtUtc = later,
            LastVerifiedAtUtc = later
        };

        await repository.SaveToolInstanceAsync(first, cancellationToken);
        await repository.SaveToolInstanceAsync(second, cancellationToken);

        var stored = await repository.GetToolInstanceAsync(
            first.ToolInstanceId,
            cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(originalFirstObserved, stored.FirstObservedAtUtc);
        Assert.Equal(later, stored.LastVerifiedAtUtc);
        Assert.Equal(second.ObservedPath, stored.ObservedPath);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private SqliteAtlasRepository CreateRepository() =>
        new(_databasePath, _backupDirectory);

    private ManagedToolInstallation CreateInstallation(
        DateTimeOffset installedAtUtc,
        DateTimeOffset lastVerifiedAtUtc) =>
        new(
            SchemaVersion: 1,
            ToolId: "cpp2il",
            DisplayName: "Cpp2IL",
            Version: "test-version",
            Platform: "win-x64",
            DefinitionDigest: new string('d', 64),
            PackageSha256: new string('a', 64),
            ExecutableSha256: new string('b', 64),
            RootPath: Path.Combine(
                _temporaryDirectory,
                "tools",
                "cpp2il",
                "test-version"),
            Status: ToolInstallationStatus.Verified,
            InstalledAtUtc: installedAtUtc,
            LastVerifiedAtUtc: lastVerifiedAtUtc,
            ProbeResults:
            [
                new ToolProbeResult(
                    ProbeId: "help",
                    Succeeded: true,
                    ExitCode: 0,
                    TimedOut: false,
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    FailureCode: null,
                    FailureMessage: null)
            ],
            ReplacedInstallationPath: null);

    private static ToolInstance CreateToolInstance(
        ManagedToolInstallation installation,
        string observedPath,
        DateTimeOffset firstObservedAtUtc,
        DateTimeOffset lastVerifiedAtUtc)
    {
        var id = ToolInstanceId.Create(
            installation.ToolId,
            installation.ExecutableSha256,
            installation.Platform,
            ToolTrustLevel.ManagedPinned);
        return new ToolInstance(
            ToolInstanceId: id,
            ToolName: installation.ToolId,
            VersionLabel: installation.Version,
            Platform: installation.Platform,
            TrustLevel: ToolTrustLevel.ManagedPinned,
            DefinitionDigest: installation.DefinitionDigest,
            PackageSha256: installation.PackageSha256,
            ExecutableSha256: installation.ExecutableSha256,
            ObservedPath: observedPath,
            FirstObservedAtUtc: firstObservedAtUtc,
            LastVerifiedAtUtc: lastVerifiedAtUtc,
            Status: ToolInstallationStatus.Verified);
    }
}
