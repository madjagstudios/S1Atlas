using S1Atlas.Application.Readiness;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Tools;
using Xunit;

namespace S1Atlas.Indexing.Tests.Readiness;

internal sealed class ScriptedToolReader : IManagedToolStatusReader
{
    public IReadOnlyList<ManagedToolStatus> Statuses { get; set; } = [];

    public Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(Statuses);
}

/// <summary>
/// Walks the required pipeline end to end: an empty data root reports
/// <c>scan</c>, and each completed stage advances the next step until the
/// atlas reports ready.
/// </summary>
public sealed class ReadinessProgressionTests
{
    private const string BuildId = "build-progression";

    [Fact]
    public async Task NextStep_AdvancesScanToolsExtractIndexReady()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var tools = new ScriptedToolReader
        {
            Statuses =
            [
                ReadinessFixtures.UnverifiedTool("cpp2il", ToolInstallationStatus.NotInstalled),
                ReadinessFixtures.UnverifiedTool("unity-classdata", ToolInstallationStatus.NotInstalled)
            ]
        };
        var service = new AtlasReadinessService(
            new StubSchemaInspector(ReadinessFixtures.CurrentSchema),
            new StubRuntimeProbe(ReadinessFixtures.SupportedRuntime),
            new StubLocator(GameInstall()),
            new StubMetadataReader(LiveObservation()),
            harness.Repository,
            harness.Repository,
            harness.CreatePreferredResolver(),
            harness.Repository,
            tools,
            new StubUpstreamCache(new Dictionary<CodebaseKind, IReadOnlyList<string>>()));

        var empty = await service.EvaluateAsync(CancellationToken.None);
        Assert.False(empty.IsReady);
        Assert.Equal(ReadinessFixCommands.Scan, empty.NextStep.Command);

        await harness.SeedCurrentBuildAsync(BuildId);
        var scanned = await service.EvaluateAsync(CancellationToken.None);
        Assert.False(scanned.IsReady);
        Assert.Equal(
            ReadinessFixCommands.InstallTool(ReadinessFixCommands.Cpp2IlToolId),
            scanned.NextStep.Command);

        tools.Statuses =
        [
            ReadinessFixtures.VerifiedTool("cpp2il"),
            ReadinessFixtures.VerifiedTool("unity-classdata")
        ];
        var tooled = await service.EvaluateAsync(CancellationToken.None);
        Assert.False(tooled.IsReady);
        Assert.Equal(ReadinessFixCommands.Extract, tooled.NextStep.Command);

        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync(BuildId);
        var extracted = await service.EvaluateAsync(CancellationToken.None);
        Assert.False(extracted.IsReady);
        Assert.Equal(ReadinessFixCommands.Index, extracted.NextStep.Command);

        await harness.SeedCompletedInstalledIndexAsync(extractionId, BuildId);
        var ready = await service.EvaluateAsync(CancellationToken.None);
        Assert.True(ready.IsReady);
        Assert.Equal("Ready", ready.NextStep.Summary);
    }

    private static ScheduleOneInstallation GameInstall() =>
        new(
            $"C:\\game\\{BuildId}",
            $"C:\\game\\{BuildId}\\Schedule I.exe",
            $"C:\\game\\{BuildId}\\GameAssembly.dll",
            $"C:\\game\\{BuildId}\\global-metadata.dat",
            $"C:\\game\\{BuildId}\\Mods",
            $"C:\\game\\{BuildId}\\MelonLoader");

    private static InstallationObservation LiveObservation() =>
        new(
            "2022.3",
            "3164500",
            BuildId,
            $"C:\\game\\{BuildId}",
            $"C:\\game\\{BuildId}\\GameAssembly.dll",
            $"C:\\game\\{BuildId}\\global-metadata.dat");
}
