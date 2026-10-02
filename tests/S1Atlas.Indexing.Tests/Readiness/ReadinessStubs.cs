using S1Atlas.Application.Readiness;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Indexing.Tests.Readiness;

internal sealed class StubSchemaInspector(AtlasSchemaStatus status) : IAtlasSchemaInspector
{
    public Task<AtlasSchemaStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(status);
}

internal sealed class StubRuntimeProbe(DotNetRuntimeInfo info) : IDotNetRuntimeProbe
{
    public DotNetRuntimeInfo GetCurrent() => info;
}

internal sealed class StubLocator(ScheduleOneInstallation? installation) : IScheduleOneLocator
{
    public Task<ScheduleOneInstallation?> LocateAsync(
        string? overridePath,
        CancellationToken cancellationToken) =>
        Task.FromResult(installation);
}

internal sealed class StubMetadataReader(InstallationObservation observation) : IInstallationMetadataReader
{
    public Task<InstallationObservation> ReadAsync(
        ScheduleOneInstallation installation,
        CancellationToken cancellationToken) =>
        Task.FromResult(observation);
}

internal sealed class StubToolReader(IReadOnlyList<ManagedToolStatus> statuses) : IManagedToolStatusReader
{
    public Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(statuses);
}

internal sealed class ThrowingToolReader(Exception exception) : IManagedToolStatusReader
{
    public Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<ManagedToolStatus>>(exception);
}

internal sealed class StubUpstreamCache(
    IReadOnlyDictionary<CodebaseKind, IReadOnlyList<string>> commits) : IUpstreamCommitCache
{
    public IReadOnlyList<string> GetCachedCommits(CodebaseKind codebase) =>
        commits.TryGetValue(codebase, out var list) ? list : [];
}

internal static class ReadinessFixtures
{
    public static readonly AtlasSchemaStatus CurrentSchema =
        new(AtlasSchemaStatusKind.Current, null, 0);

    public static readonly DotNetRuntimeInfo SupportedRuntime =
        new(true, "8.0.0", ".NET 8.0.0");

    public static readonly DotNetRuntimeInfo UnsupportedRuntime =
        new(false, "6.0.0", ".NET 6.0.0");

    public static ManagedToolStatus VerifiedTool(string toolId)
    {
        var definition = FakeDefinition(toolId);
        return new ManagedToolStatus(
            new ResolvedToolDefinition(definition, "digest-" + toolId),
            ToolInstallationStatus.Verified,
            new ManagedToolInstallation(
                1,
                toolId,
                toolId,
                definition.Version,
                definition.Platform,
                "digest-" + toolId,
                "package-" + toolId,
                "executable-" + toolId,
                Path.Combine(Path.GetTempPath(), "s1atlas-readiness-tool-" + toolId),
                ToolInstallationStatus.Verified,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                [],
                null),
            null,
            null);
    }

    public static ManagedToolStatus UnverifiedTool(
        string toolId,
        ToolInstallationStatus status,
        string? diagnosticMessage = null)
    {
        var definition = FakeDefinition(toolId);
        return new ManagedToolStatus(
            new ResolvedToolDefinition(definition, "digest-" + toolId),
            status,
            null,
            "TestDiagnostic",
            diagnosticMessage ?? $"{toolId} is {status}.");
    }

    public static EnvironmentSnapshot Snapshot(
        string buildId,
        InstallationObservation installation,
        DateTimeOffset capturedAtUtc) =>
        new(
            IdentityVersion: 2,
            Build: new GameBuild(
                buildId,
                "assembly-" + buildId,
                "metadata-" + buildId,
                capturedAtUtc,
                IsValid: true),
            Installation: installation,
            Dependencies: [],
            AtlasVersion: "readiness-test",
            CapturedAtUtc: capturedAtUtc);

    public static async Task SeedCompletedCodeIndexAsync(
        SqliteAtlasRepository repository,
        CodebaseKind codebase,
        CodeChannel channel,
        string sourceIdentity,
        string? environmentSnapshotId,
        string indexId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        const string createdAtUtc = "2026-09-01T00:20:00.0000000+00:00";
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                codebase,
                channel,
                sourceIdentity,
                createdAtUtc,
                environmentSnapshotId),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(
                indexId,
                snapshotId,
                IndexRunStatus.Running,
                createdAtUtc),
            cancellationToken);
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [
                    new IndexSymbolRecord(
                        "symbol-" + indexId,
                        snapshotId,
                        $"{codebase}:{channel}:Type:Readiness.Probe",
                        "Type",
                        "Readiness.Probe",
                        "Readiness.Probe",
                        false)
                ],
                [],
                [],
                [],
                []),
            "2026-09-01T00:21:00.0000000+00:00",
            cancellationToken);
    }

    public static async Task<string> SeedCompletedSceneSnapshotAsync(
        SqliteAtlasRepository repository,
        string databasePath,
        string buildId,
        string sceneSnapshotId,
        string extractionId,
        string codeSnapshotId,
        string indexId,
        CancellationToken cancellationToken = default)
    {
        var inputSnapshotId = "input-" + sceneSnapshotId;
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var link = connection.CreateCommand();
        link.CommandText = """
            INSERT INTO input_snapshots (
                input_snapshot_id, build_id, root_path, manifest_digest,
                created_at_utc, replay_verified, replay_verified_at_utc)
            VALUES ($id, $build, $root, $digest, $created, 1, $created);
            UPDATE extraction_attempts
            SET input_snapshot_id = $id
            WHERE attempt_id = (
                SELECT source_attempt_id
                FROM validated_extractions
                WHERE extraction_id = $extraction);
            UPDATE code_snapshots
            SET environment_snapshot_id = (
                SELECT snapshot_id
                FROM environment_snapshots
                WHERE build_id = $build
                LIMIT 1)
            WHERE snapshot_id = $code;
            """;
        link.Parameters.AddWithValue("$id", inputSnapshotId);
        link.Parameters.AddWithValue("$build", buildId);
        link.Parameters.AddWithValue("$root", "game-root");
        link.Parameters.AddWithValue("$digest", "manifest");
        link.Parameters.AddWithValue("$created", "2026-09-01T00:29:00.0000000+00:00");
        link.Parameters.AddWithValue("$extraction", extractionId);
        link.Parameters.AddWithValue("$code", codeSnapshotId);
        await link.ExecuteNonQueryAsync(cancellationToken);
        var snapshot = new SceneSnapshotRecord(
            sceneSnapshotId,
            buildId,
            extractionId,
            inputSnapshotId,
            codeSnapshotId,
            indexId,
            "fixture-parser",
            "1",
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            SceneSnapshotStatus.Running,
            SceneRecoveryStatus.FullyRecovered,
            "2026-09-01T00:30:00.0000000+00:00");
        await repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await repository.CompleteSceneSnapshotAsync(
            sceneSnapshotId,
            new SceneWriteSet(snapshot, [], [], [], [], [], [], [], []),
            "2026-09-01T00:31:00.0000000+00:00",
            cancellationToken);
        await repository.PublishSceneSnapshotAsync(
            sceneSnapshotId,
            "2026-09-01T00:32:00.0000000+00:00",
            cancellationToken);
        return sceneSnapshotId;
    }

    public static async Task<string> SeedCompletedReferenceIndexAsync(
        SqliteAtlasRepository repository,
        string collection,
        string indexId,
        string snapshotId,
        string buildId,
        string gameIndexId,
        CancellationToken cancellationToken = default)
    {
        const string startedAtUtc = "2026-09-01T00:40:00.0000000+00:00";
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.ReferenceMod,
                CodeChannel.Installed,
                collection,
                startedAtUtc),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, startedAtUtc),
            cancellationToken);
        var symbolId = indexId + "-symbol";
        await repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [new IndexSymbolRecord(
                    symbolId,
                    snapshotId,
                    "ReferenceMod:Installed:Method:qol/Qol.Mod::Run():System.Void",
                    "Method",
                    "qol/Qol.Mod::Run():System.Void",
                    "qol/Qol.Mod::Run():System.Void",
                    false)],
                [],
                [],
                [],
                [],
                ReferenceIndexContext: new ReferenceIndexContextRecord(indexId, gameIndexId, buildId),
                ReferenceMods:
                [new IndexReferenceModRecord(
                    "qol",
                    "Quality of Life",
                    "1.0.0",
                    "MIT",
                    "reference-input",
                    indexId + "-content",
                    [symbolId])]),
            "2026-09-01T00:41:00.0000000+00:00",
            cancellationToken);
        return indexId;
    }

    public static ResolvedToolDefinition FakeResolvedDefinition(
        string toolId,
        string platform = "win-x64")
    {
        var definition = FakeDefinition(toolId) with { Platform = platform };
        return new ResolvedToolDefinition(definition, "digest-" + toolId);
    }

    private static ToolDefinition FakeDefinition(string toolId) =>
        new(
            1,
            toolId,
            toolId,
            "test-version",
            "win-x64",
            new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                null,
                new Uri("https://example.invalid/" + toolId),
                new Uri("https://example.invalid/" + toolId + "/releases"),
                toolId,
                1,
                "package-sha",
                toolId,
                new ToolSafetyLimits(1, 1, 1)),
            new ToolLicenseDefinition("MIT", new Uri("https://example.invalid/license")),
            []);
}
