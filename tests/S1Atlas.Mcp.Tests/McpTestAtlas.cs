using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Mcp.Tests;

internal sealed class McpTestAtlas : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-16T00:00:00Z");

    private const string ToolInstanceId = "tool-instance-1";
    private const string BuildId = "build-a";
    private const string RecipeId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private McpTestAtlas(string root)
    {
        _root = root;
        DataRoot = root;
        _repository = new SqliteAtlasRepository(
            Path.Combine(root, "atlas.db"),
            Path.Combine(root, "backups"));
    }

    public string DataRoot { get; }

    public static async Task<McpTestAtlas> SeedHealthyInstalledBuildAsync(string buildId = BuildId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var atlas = new McpTestAtlas(root);
        await atlas.InitializeAsync(CancellationToken.None);
        await atlas.SeedCurrentBuildAsync(buildId);
        var seeded = await atlas.SeedValidatedExtractionAsync(buildId);
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                seeded.Extraction.ExtractionId,
                seeded.Report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
        var indexId = "index-" + seeded.Extraction.ExtractionId;
        await atlas.SeedCompletedInstalledIndexAsync(
            seeded.Extraction.ExtractionId,
            buildId,
            indexId);
        return atlas;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataRoot);
        await _repository.InitializeAsync(cancellationToken);
        await SeedToolInstanceAsync(cancellationToken);
    }

    private async Task SeedCurrentBuildAsync(string buildId)
    {
        await _repository.SaveSnapshotAsync(CreateSnapshot(buildId), CancellationToken.None);
    }

    private async Task SeedCompletedInstalledIndexAsync(
        string extractionId,
        string buildId,
        string indexId)
    {
        var ct = CancellationToken.None;
        var snapshotId = "snapshot-" + extractionId;
        var createdAtUtc = BaseTime.AddMinutes(20).ToString("O");
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                extractionId,
                createdAtUtc),
            ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                indexId,
                snapshotId,
                IndexRunStatus.Running,
                createdAtUtc),
            ct);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [
                    new IndexSymbolRecord(
                        "symbol-" + extractionId,
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.Authority",
                        "Type",
                        "Demo.Authority",
                        "Demo.Authority",
                        false)
                ],
                [],
                [],
                [],
                []),
            BaseTime.AddMinutes(21).ToString("O"),
            ct);
    }

    private async Task<SeededExtraction> SeedValidatedExtractionAsync(string buildId)
    {
        var manifest = CreateManifest();
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(RecipeId, digest);
        var attempt = await AdvanceAttemptToValidatingAsync(
            buildId,
            extractionId[..32],
            CancellationToken.None);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0,
            TotalOutputBytes: 6,
            TotalManagedBytes: 6,
            Assemblies:
            [
                new AssemblyIdentityStatistics(
                    "Assembly-CSharp",
                    1,
                    6,
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
        var extraction = new ValidatedExtraction(
            extractionId,
            RecipeId,
            buildId,
            ToolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            ProfileDigest,
            1,
            1,
            digest,
            GetFinalExtractionRoot(buildId, extractionId),
            BaseTime.AddMinutes(10),
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            statistics);
        var report = new ValidationReport(
            1,
            attempt.AttemptId,
            ValidationSubjectKind.CandidateOutput,
            null,
            buildId,
            RecipeId,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            ValidationOutcome.Valid,
            true,
            true,
            true,
            digest,
            statistics,
            null,
            [],
            [],
            true,
            BaseTime.AddMinutes(11));
        var promotion = new ValidatedExtractionPromotion(
            attempt with
            {
                Status = ExtractionAttemptStatus.Succeeded,
                CompletedAtUtc = BaseTime.AddMinutes(11),
                ResultExtractionId = extractionId
            },
            extraction,
            manifest,
            report,
            AutomaticPreferenceReason: null);

        await WriteFinalDocumentsAsync(extraction, manifest, report);
        await _repository.CommitValidatedExtractionAsync(promotion, CancellationToken.None);

        return new SeededExtraction(extraction, report);
    }

    private async Task WriteFinalDocumentsAsync(
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report)
    {
        var documentsRoot = GetFinalExtractionRoot(
            extraction.BuildId,
            extraction.ExtractionId);
        var reconstructedRoot = Path.Combine(documentsRoot, "reconstructed");
        Directory.CreateDirectory(reconstructedRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(reconstructedRoot, "Assembly-CSharp.dll"),
            [10, 20, 30, 40, 50, 60],
            CancellationToken.None);

        await WriteValidatedExtractionDocumentsAsync(
            documentsRoot,
            extraction,
            manifest,
            report,
            CancellationToken.None);
    }

    private async Task<ExtractionAttempt> AdvanceAttemptToValidatingAsync(
        string buildId,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var created = new ExtractionAttempt(
            AttemptId: attemptId,
            RecipeId: RecipeId,
            BuildId: buildId,
            ToolInstanceId: ToolInstanceId,
            ProfileId: "default-profile",
            ProfileVersion: 1,
            ProfileDigest: ProfileDigest,
            ValidationPolicyId: "managed-assemblies-v1",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: PolicyDigest,
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: ExtractionInputSource.Live,
            InputSnapshotId: null,
            Status: ExtractionAttemptStatus.Created,
            CreatedAtUtc: BaseTime,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: $"C:\\attempts\\{attemptId}\\work",
            StandardOutputPath: $"C:\\attempts\\{attemptId}\\logs\\stdout.log",
            StandardErrorPath: $"C:\\attempts\\{attemptId}\\logs\\stderr.log",
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: null,
            ProcessExitCode: null,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            KeepFailedArtifacts: false,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: null,
            ResultExtractionId: null);
        await _repository.CreateAttemptAsync(created, cancellationToken);

        var preparing = created with
        {
            Status = ExtractionAttemptStatus.Preparing,
            StartedAtUtc = BaseTime
        };
        await _repository.TransitionAttemptAsync(
            preparing,
            ExtractionAttemptStatus.Created,
            cancellationToken);

        var running = preparing with
        {
            Status = ExtractionAttemptStatus.Running,
            ProcessId = 1234
        };
        await _repository.TransitionAttemptAsync(
            running,
            ExtractionAttemptStatus.Preparing,
            cancellationToken);

        var processCompleted = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = $"C:\\attempts\\{attemptId}\\candidate-output"
        };
        await _repository.TransitionAttemptAsync(
            processCompleted,
            ExtractionAttemptStatus.Running,
            cancellationToken);

        var validating = processCompleted with
        {
            Status = ExtractionAttemptStatus.Validating
        };
        await _repository.TransitionAttemptAsync(
            validating,
            ExtractionAttemptStatus.ProcessCompleted,
            cancellationToken);
        return validating;
    }

    private async Task SeedToolInstanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "atlas.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                $toolInstanceId, 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-16T00:00:00.0000000+00:00',
                '2026-08-16T00:05:00.0000000+00:00', 'Verified');
            """;
        command.Parameters.AddWithValue("$toolInstanceId", ToolInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EnvironmentSnapshot CreateSnapshot(string buildId) =>
        new(
            IdentityVersion: 2,
            Build: new GameBuild(
                buildId,
                "assembly-" + buildId,
                "metadata-" + buildId,
                BaseTime,
                IsValid: true),
            Installation: new InstallationObservation(
                "2022.3",
                "3164500",
                buildId,
                $"C:\\game\\{buildId}",
                $"C:\\game\\{buildId}\\GameAssembly.dll",
                $"C:\\game\\{buildId}\\global-metadata.dat"),
            Dependencies: [],
            AtlasVersion: "0.2.0-test",
            CapturedAtUtc: BaseTime);

    private static ArtifactManifest CreateManifest()
    {
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData([10, 20, 30, 40, 50, 60]))
            .ToLowerInvariant();
        return new ArtifactManifest(
            1,
            [
                new ArtifactManifestEntry(
                    "reconstructed/Assembly-CSharp.dll",
                    ArtifactKind.ManagedAssembly,
                    6,
                    sha,
                    "Assembly-CSharp",
                    "Assembly-CSharp.dll",
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
    }

    private sealed record SeededExtraction(
        ValidatedExtraction Extraction,
        ValidationReport Report);

    private string GetFinalExtractionRoot(string buildId, string extractionId) =>
        Path.Combine(DataRoot, "builds", buildId, "extractions", extractionId);

    private async Task WriteValidatedExtractionDocumentsAsync(
        string documentsRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report,
        CancellationToken cancellationToken)
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
            [DataRoot, documentsRoot, extraction, manifest, report, cancellationToken])!;
        await writeTask;
    }
}
