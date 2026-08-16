using System.Security.Cryptography;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class CliQueryParityTests
{
    [Fact]
    public async Task Search_ScheduleI_UsesPreferredVerifiedIndex_NotNewerNonPreferred()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();

        var result = CliRunner.Run(atlas.DataRoot, "search", "Beta", "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("SymbolNotFound", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ScheduleI_FindsAlphaInPreferredIndex()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();

        var result = CliRunner.Run(atlas.DataRoot, "search", "Alpha", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alpha", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("index-newer", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ScheduleI_AllChannels_UsesPreferredVerifiedIndex()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();

        var preferred = CliRunner.Run(
            atlas.DataRoot,
            "search",
            "Alpha",
            "--codebase",
            "schedule-i",
            "--channel",
            "all",
            "--json");
        var nonPreferred = CliRunner.Run(
            atlas.DataRoot,
            "search",
            "Beta",
            "--codebase",
            "schedule-i",
            "--channel",
            "all",
            "--json");

        Assert.Equal(0, preferred.ExitCode);
        Assert.Contains("Alpha", preferred.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, nonPreferred.ExitCode);
        Assert.Contains("SymbolNotFound", nonPreferred.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Api_S1Api_PathUnchanged()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();

        var result = CliRunner.Run(atlas.DataRoot, "search", "ApiOnly", "--codebase", "s1api", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ApiOnly", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ScheduleI_ExplicitBuild_SelectsThatBuild()
    {
        await using var atlas = await CliParityAtlas.SeedPreferredPlusNewerNonPreferredAsync();

        var result = CliRunner.Run(atlas.DataRoot, "search", "Gamma", "--build", CliParityAtlas.ExplicitBuildId, "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Gamma", result.StandardOutput, StringComparison.Ordinal);
    }
}

internal static class CliRunner
{
    public static (int ExitCode, string StandardOutput, string StandardError) Run(string dataRoot, params string[] args)
    {
        var application = new CliApplication(dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }
}

internal sealed class CliParityAtlas : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
    private const string BuildId = "build-current";
    public const string ExplicitBuildId = "build-explicit";
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private CliParityAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }

    public static async Task<CliParityAtlas> SeedPreferredPlusNewerNonPreferredAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-cli-parity-" + Guid.NewGuid().ToString("N"));
        var atlas = new CliParityAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(CancellationToken.None);
        await atlas.SeedToolInstanceAsync();
        await atlas.SeedSnapshotAsync(ExplicitBuildId);
        var explicitExtraction = await atlas.SeedValidatedExtractionAsync(ExplicitBuildId, "3");
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(ExplicitBuildId, explicitExtraction, BaseTime.AddMinutes(11), ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
        await atlas.SeedCompletedIndexAsync(explicitExtraction, "index-explicit", "Gamma", BaseTime.AddMinutes(15));
        await atlas.SeedSnapshotAsync(BuildId);

        var preferred = await atlas.SeedValidatedExtractionAsync(BuildId, "1");
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(BuildId, preferred, BaseTime.AddMinutes(11), ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
        await atlas.SeedCompletedIndexAsync(preferred, "index-preferred", "Alpha", BaseTime.AddMinutes(20));

        var nonPreferred = await atlas.SeedValidatedExtractionAsync(BuildId, "2");
        await atlas.SeedCompletedIndexAsync(nonPreferred, "index-newer", "Beta", BaseTime.AddMinutes(30));
        await atlas.SeedCompletedIndexAsync("api-fixture", "index-api", "ApiOnly", BaseTime.AddMinutes(40), CodebaseKind.S1Api);
        return atlas;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private async Task<string> SeedValidatedExtractionAsync(string buildId, string seed)
    {
        var recipeId = seed.PadLeft(64, seed[0]);
        var manifest = new ArtifactManifest(1, [
            new ArtifactManifestEntry("reconstructed/Assembly-CSharp.dll", ArtifactKind.ManagedAssembly, 6,
                Convert.ToHexString(SHA256.HashData([10, 20, 30, 40, 50, 60])).ToLowerInvariant(),
                "Assembly-CSharp", "Assembly-CSharp.dll", 1, 1, 0, 0, 0)
        ]);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var attempt = await CreateValidatingAttemptAsync(buildId, recipeId, extractionId[..32]);
        var statistics = new ExtractionStatistics(1, 1, 1, 1, 1, 0, 0, 0, 6, 6,
            [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 6, 1, 1, 0, 0, 0)]);
        var extractionRoot = Path.Combine(DataRoot, "builds", buildId, "extractions", extractionId);
        var extraction = new ValidatedExtraction(extractionId, recipeId, buildId, ToolInstanceId, attempt.AttemptId,
            "default-profile", 1, ProfileDigest, 1, 1, digest, extractionRoot, BaseTime.AddMinutes(10),
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, statistics);
        var report = new ValidationReport(1, attempt.AttemptId, ValidationSubjectKind.CandidateOutput, null, buildId,
            recipeId, "managed-assemblies-v1", 1, PolicyDigest, ValidationOutcome.Valid, true, true, true, digest,
            statistics, null, [], [], true, BaseTime.AddMinutes(11));
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        await File.WriteAllBytesAsync(Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"), [10, 20, 30, 40, 50, 60]);
        await WriteValidatedExtractionDocumentsAsync(extractionRoot, extraction, manifest, report);
        await _repository.CommitValidatedExtractionAsync(new ValidatedExtractionPromotion(
            attempt with { Status = ExtractionAttemptStatus.Succeeded, CompletedAtUtc = BaseTime.AddMinutes(11), ResultExtractionId = extractionId },
            extraction, manifest, report, null), CancellationToken.None);
        return extractionId;
    }

    private async Task SeedCompletedIndexAsync(
        string sourceIdentity,
        string indexId,
        string symbolName,
        DateTimeOffset completedAt,
        CodebaseKind codebase = CodebaseKind.ScheduleI)
    {
        var snapshotId = "snapshot-" + indexId;
        await _repository.CreateCodeSnapshotAsync(new CodeSnapshotRecord(snapshotId, codebase, CodeChannel.Installed,
            sourceIdentity, completedAt.AddMinutes(-1).ToString("O")), CancellationToken.None);
        await _repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, completedAt.AddMinutes(-1).ToString("O")), CancellationToken.None);
        await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet([
            new IndexSymbolRecord("symbol-" + indexId, snapshotId, codebase + ":Installed:Type:" + symbolName,
                "Type", symbolName, symbolName, false)
        ], [], [], [], []), completedAt.ToString("O"), CancellationToken.None);
    }

    private async Task<ExtractionAttempt> CreateValidatingAttemptAsync(string buildId, string recipeId, string attemptId)
    {
        var created = new ExtractionAttempt(attemptId, recipeId, buildId, ToolInstanceId, "default-profile", 1,
            ProfileDigest, "managed-assemblies-v1", 1, PolicyDigest, 1, 1, ExtractionInputSource.Live, null,
            ExtractionAttemptStatus.Created, BaseTime, null, null, null, null, $"C:\\attempts\\{attemptId}\\work",
            $"C:\\attempts\\{attemptId}\\stdout.log", $"C:\\attempts\\{attemptId}\\stderr.log", false, false, 0, 0,
            null, null, null, null, null, false, 0, 0, null, null);
        await _repository.CreateAttemptAsync(created, CancellationToken.None);
        var preparing = created with { Status = ExtractionAttemptStatus.Preparing, StartedAtUtc = BaseTime };
        await _repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, CancellationToken.None);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 1234 };
        await _repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, CancellationToken.None);
        var completed = running with { Status = ExtractionAttemptStatus.ProcessCompleted, ProcessExitCode = 0, CandidateOutputPath = "C:\\candidate" };
        await _repository.TransitionAttemptAsync(completed, ExtractionAttemptStatus.Running, CancellationToken.None);
        var validating = completed with { Status = ExtractionAttemptStatus.Validating };
        await _repository.TransitionAttemptAsync(validating, ExtractionAttemptStatus.ProcessCompleted, CancellationToken.None);
        return validating;
    }

    private Task SeedSnapshotAsync(string buildId) => _repository.SaveSnapshotAsync(new EnvironmentSnapshot(2,
        new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, BaseTime, true),
        new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null), [], "0.1.0-test", BaseTime), CancellationToken.None);

    private async Task SeedToolInstanceAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(DataRoot, "atlas.db"), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO tool_instances (tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ($id, 'cpp2il', 'test', 'win-x64', 'ManagedPinned', 'definition', 'package', 'executable', 'C:\\tools\\Cpp2IL.exe', '2026-08-16T00:00:00.0000000+00:00', '2026-08-16T00:05:00.0000000+00:00', 'Verified');""";
        command.Parameters.AddWithValue("$id", ToolInstanceId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteValidatedExtractionDocumentsAsync(
        string extractionRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report)
    {
        var extractionAssembly = typeof(ValidatedExtractionIntegrityVerifier).Assembly;
        var storeType = extractionAssembly.GetType(
            "S1Atlas.Extraction.Manifests.ValidatedExtractionDocumentStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(storeType)
            ?? throw new InvalidOperationException("Could not create validated extraction document store.");
        var writeMethod = storeType.GetMethod("WriteFinalDocumentsAsync")
            ?? throw new InvalidOperationException("Validated extraction document writer was not found.");
        var writeTask = (Task)writeMethod.Invoke(
            store,
            [DataRoot, extractionRoot, extraction, manifest, report, CancellationToken.None])!;
        await writeTask;
    }
}
