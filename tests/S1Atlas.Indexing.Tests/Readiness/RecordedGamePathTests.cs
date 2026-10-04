using S1Atlas.Application.Readiness;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Readiness;

// Recorded-install readiness (AT-120): the scan check locates the install
// recorded in the snapshot (B) instead of Steam discovery (A).
public sealed class RecordedGamePathTests : IAsyncDisposable
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    private const string BuildId = "build-recorded-path";
    private const string SteamBuildId = "steam-build-1";
    private const string ExecutableVersion = "2022.3.62f1";

    private readonly string _installA;
    private readonly string _installB;
    private readonly string _assemblyB;
    private readonly string _metadataB;

    public RecordedGamePathTests()
    {
        var tag = Guid.NewGuid().ToString("N");
        _installA = Path.Combine(Path.GetTempPath(), "s1atlas-game-a-" + tag);
        _installB = Path.Combine(Path.GetTempPath(), "s1atlas-game-b (mod copy)-" + tag);
        Directory.CreateDirectory(_installA);
        Directory.CreateDirectory(_installB);
        _assemblyB = Path.Combine(_installB, "GameAssembly.dll");
        _metadataB = Path.Combine(_installB, "global-metadata.dat");
        File.WriteAllBytes(_assemblyB, [10, 20, 30, 40, 50, 60]);
        File.WriteAllBytes(_metadataB, [1, 2, 3, 4]);
        Freeze(CapturedAt.AddHours(-1));
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_installA);
        await TestDirectory.DeleteTreeAsync(_installB);
    }

    [Fact]
    public async Task RecordedInstallUnchanged_ReportsOkAndLocatesRecordedRoot()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            SnapshotFor(_installB), CancellationToken.None);
        var locator = new PathAwareLocator(InstallAt(_installA));
        var service = CreateService(harness, locator, LiveObservation);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Ok, scan.State);
        Assert.Contains(locator.RequestedPaths, requested => requested == _installB);
    }

    [Fact]
    public async Task RecordedInstallSteamBuildChanged_ReportsStaleWithExplicitFix()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            SnapshotFor(_installB), CancellationToken.None);
        var locator = new PathAwareLocator(InstallAt(_installA));
        var service = CreateService(
            harness,
            locator,
            install => LiveObservation(install) with { SteamBuildId = "steam-build-2" });

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains(SteamBuildId, scan.Detail, StringComparison.Ordinal);
        Assert.Contains("steam-build-2", scan.Detail, StringComparison.Ordinal);
        Assert.Equal($"s1atlas scan --game-path \"{_installB}\"", scan.FixCommand);
        Assert.Equal(_installB, scan.ScanGamePath);
        Assert.Null(scan.ScanRecordedGamePath);
    }

    [Fact]
    public async Task RecordedInstallFilesModified_ReportsStaleWithExplicitFix()
    {
        File.SetLastWriteTimeUtc(_assemblyB, CapturedAt.AddHours(1).UtcDateTime);
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            SnapshotFor(_installB), CancellationToken.None);
        var locator = new PathAwareLocator(InstallAt(_installA));
        var service = CreateService(harness, locator, LiveObservation);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains("modified after the scan", scan.Detail, StringComparison.Ordinal);
        Assert.Equal($"s1atlas scan --game-path \"{_installB}\"", scan.FixCommand);
        Assert.Equal(_installB, scan.ScanGamePath);
    }

    [Fact]
    public async Task RecordedInstallMissing_DiscoveryFound_NamesBothAndTargetsDiscovered()
    {
        var missingB = Path.Combine(Path.GetTempPath(), "s1atlas-game-gone-" + Guid.NewGuid().ToString("N"));
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            SnapshotFor(missingB), CancellationToken.None);
        var locator = new PathAwareLocator(InstallAt(_installA));
        var service = CreateService(harness, locator, LiveObservation);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains(missingB, scan.Detail, StringComparison.Ordinal);
        Assert.Contains(_installA, scan.Detail, StringComparison.Ordinal);
        Assert.Equal($"s1atlas scan --game-path \"{_installA}\"", scan.FixCommand);
        Assert.Equal(_installA, scan.ScanGamePath);
        Assert.Equal(missingB, scan.ScanRecordedGamePath);
    }

    [Fact]
    public async Task RecordedInstallMissing_NoDiscovery_UsesFolderPlaceholder()
    {
        var missingB = Path.Combine(Path.GetTempPath(), "s1atlas-game-gone-" + Guid.NewGuid().ToString("N"));
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.Repository.SaveSnapshotAsync(
            SnapshotFor(missingB), CancellationToken.None);
        var locator = new PathAwareLocator(null);
        var service = CreateService(harness, locator, LiveObservation);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Stale, scan.State);
        Assert.Contains(missingB, scan.Detail, StringComparison.Ordinal);
        Assert.Contains("--game-path <folder>", scan.Detail, StringComparison.Ordinal);
        Assert.Null(scan.FixCommand);
        Assert.Null(scan.ScanGamePath);
    }

    [Fact]
    public async Task NoSnapshot_UsesDiscovery()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var locator = new PathAwareLocator(InstallAt(_installA));
        var service = CreateService(harness, locator, LiveObservation);

        var report = await service.EvaluateAsync(CancellationToken.None);

        var scan = Item(report, ReadinessItemIds.Scan);
        Assert.Equal(ReadinessState.Missing, scan.State);
        Assert.Equal(ReadinessFixCommands.Scan, scan.FixCommand);
        Assert.Null(scan.ScanGamePath);
        Assert.Null(Assert.Single(locator.RequestedPaths));
    }

    private EnvironmentSnapshot SnapshotFor(string installationRoot) =>
        ReadinessFixtures.Snapshot(
            BuildId,
            new InstallationObservation(
                ExecutableVersion,
                "3164500",
                SteamBuildId,
                installationRoot,
                _assemblyB,
                _metadataB),
            CapturedAt);

    private InstallationObservation LiveObservation(ScheduleOneInstallation install) =>
        new(
            ExecutableVersion,
            "3164500",
            SteamBuildId,
            install.RootPath,
            Path.Combine(install.RootPath, "GameAssembly.dll"),
            Path.Combine(install.RootPath, "global-metadata.dat"));

    private static ScheduleOneInstallation InstallAt(string root) =>
        new(
            root,
            Path.Combine(root, "Schedule I.exe"),
            Path.Combine(root, "GameAssembly.dll"),
            Path.Combine(root, "global-metadata.dat"),
            Path.Combine(root, "Mods"),
            Path.Combine(root, "MelonLoader"));

    private static AtlasReadinessService CreateService(
        AuthorityHarness harness,
        IScheduleOneLocator locator,
        Func<ScheduleOneInstallation, InstallationObservation> read) =>
        new(
            new StubSchemaInspector(ReadinessFixtures.CurrentSchema),
            new StubRuntimeProbe(ReadinessFixtures.SupportedRuntime),
            locator,
            new FuncMetadataReader(read),
            harness.Repository,
            harness.Repository,
            harness.CreatePreferredResolver(),
            harness.Repository,
            new StubToolReader(
            [
                ReadinessFixtures.VerifiedTool("cpp2il"),
                ReadinessFixtures.VerifiedTool("unity-classdata")
            ]),
            new StubUpstreamCache(new Dictionary<CodebaseKind, IReadOnlyList<string>>()));

    private static ReadinessItem Item(ReadinessReport report, string id) =>
        report.Items.Single(item => item.Id == id);

    private void Freeze(DateTimeOffset timestamp)
    {
        File.SetLastWriteTimeUtc(_assemblyB, timestamp.UtcDateTime);
        File.SetLastWriteTimeUtc(_metadataB, timestamp.UtcDateTime);
    }

    // Discovery (null) finds the Steam copy; an explicit path finds
    // whatever exists at that path, like the real locator.
    private sealed class PathAwareLocator(ScheduleOneInstallation? discovered) : IScheduleOneLocator
    {
        public List<string?> RequestedPaths { get; } = [];

        public Task<ScheduleOneInstallation?> LocateAsync(
            string? overridePath,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(overridePath);
            if (overridePath is null)
            {
                return Task.FromResult(discovered);
            }

            if (!Directory.Exists(overridePath))
            {
                return Task.FromResult<ScheduleOneInstallation?>(null);
            }

            return Task.FromResult<ScheduleOneInstallation?>(InstallAt(overridePath));
        }
    }

    private sealed class FuncMetadataReader(
        Func<ScheduleOneInstallation, InstallationObservation> read) : IInstallationMetadataReader
    {
        public Task<InstallationObservation> ReadAsync(
            ScheduleOneInstallation installation,
            CancellationToken cancellationToken) =>
            Task.FromResult(read(installation));
    }
}

public sealed class ScanAtQuotingTests
{
    [Theory]
    [InlineData("C:\\Games\\Schedule I", "s1atlas scan --game-path \"C:\\Games\\Schedule I\"")]
    [InlineData("C:\\Games\\Copy (x86)", "s1atlas scan --game-path \"C:\\Games\\Copy (x86)\"")]
    public void ScanAt_QuotesPaths(string gamePath, string expected)
    {
        Assert.Equal(expected, ReadinessFixCommands.ScanAt(gamePath));
    }

    [Fact]
    public void ScanAt_PathWithQuote_ReturnsNull()
    {
        Assert.Null(ReadinessFixCommands.ScanAt("C:\\we\"ird"));
    }
}
