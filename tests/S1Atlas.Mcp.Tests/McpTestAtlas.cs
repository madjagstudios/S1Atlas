using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Storage.Sqlite;
using System.Security.Cryptography;
using System.Text;

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
    public string BuildIdValue => BuildId;
    public string IndexId { get; private set; } = string.Empty;
    public string KnownSymbolFragment => "Dealer";
    public string MethodSelector => "System.Void Demo.Widget::Run()";
    public string MethodSymbolId => "method-run";
    public string TypeSelector => "Demo.Widget";
    public string SourceRelativePath => "Assembly-CSharp.cs";
    public string SourcePath => Path.Combine(DataRoot, "builds", BuildId, "indexes", IndexId, SourceRelativePath);

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
        atlas.IndexId = indexId;
        return atlas;
    }

    public static async Task<McpTestAtlas> EmptyAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var atlas = new McpTestAtlas(root);
        await atlas.InitializeAsync(CancellationToken.None);
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

        const string sourceText = "namespace Demo;\npublic class Widget\n{\n    public void Run() { }\n}\n";
        var sourceFile = new IndexSourceFileRecord(
            "source-file-widget",
            snapshotId,
            SourceRelativePath,
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var sourceLocation = new IndexSourceLocationRecord(
            MethodSymbolId,
            sourceFile.SourceFileId,
            4,
            5,
            4,
            26);

        var indexRoot = Path.Combine(DataRoot, "builds", buildId, "indexes", indexId);
        Directory.CreateDirectory(indexRoot);
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, sourceFile.RelativePath),
            sourceText,
            new UTF8Encoding(false),
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
                        false),
                    new IndexSymbolRecord(
                        "type-widget",
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.Widget",
                        "Type",
                        TypeSelector,
                        TypeSelector,
                        false),
                    new IndexSymbolRecord(
                        MethodSymbolId,
                        snapshotId,
                        "ScheduleI:Installed:Method:Demo.Widget::Run()",
                        "Method",
                        "Demo.Widget.Run",
                        MethodSelector,
                        false,
                        BodyRecoveryStatus.Unknown),
                    new IndexSymbolRecord(
                        "type-dealer-alpha",
                        snapshotId,
                        "ScheduleI:Installed:Type:Alpha.DealerService",
                        "Type",
                        "Alpha.DealerService",
                        "Alpha.DealerService",
                        false),
                    new IndexSymbolRecord(
                        "type-dealer-beta",
                        snapshotId,
                        "ScheduleI:Installed:Type:Beta.DealerService",
                        "Type",
                        "Beta.DealerService",
                        "Beta.DealerService",
                        false),
                    new IndexSymbolRecord(
                        "method-caller",
                        snapshotId,
                        "ScheduleI:Installed:Method:Demo.Caller::Invoke()",
                        "Method",
                        "Demo.Caller.Invoke",
                        "System.Void Demo.Caller::Invoke()",
                        false,
                        BodyRecoveryStatus.Recovered),
                    new IndexSymbolRecord(
                        "method-service-execute",
                        snapshotId,
                        "ScheduleI:Installed:Method:Demo.Service::Execute()",
                        "Method",
                        "Demo.Service.Execute",
                        "System.Void Demo.Service::Execute()",
                        false,
                        BodyRecoveryStatus.Recovered),
                    new IndexSymbolRecord(
                        "method-worker-alpha",
                        snapshotId,
                        "ScheduleI:Installed:Method:Alpha.Worker::Run()",
                        "Method",
                        "Alpha.Worker.Run",
                        "System.Void Alpha.Worker::Run()",
                        false,
                        BodyRecoveryStatus.Recovered),
                    new IndexSymbolRecord(
                        "method-worker-beta",
                        snapshotId,
                        "ScheduleI:Installed:Method:Beta.Worker::Run()",
                        "Method",
                        "Beta.Worker.Run",
                        "System.Void Beta.Worker::Run()",
                        false,
                        BodyRecoveryStatus.Recovered),
                    new IndexSymbolRecord(
                        "type-base-widget",
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.WidgetBase",
                        "Type",
                        "Demo.WidgetBase",
                        "Demo.WidgetBase",
                        false),
                    new IndexSymbolRecord(
                        "type-payload",
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.Payload",
                        "Type",
                        "Demo.Payload",
                        "Demo.Payload",
                        false),
                    new IndexSymbolRecord(
                        "type-result",
                        snapshotId,
                        "ScheduleI:Installed:Type:Demo.Result",
                        "Type",
                        "Demo.Result",
                        "Demo.Result",
                        false),
                    new IndexSymbolRecord(
                        "field-state",
                        snapshotId,
                        "ScheduleI:Installed:Field:Demo.Widget::System.Int32 _state",
                        "Field",
                        "Demo.Widget._state",
                        "System.Int32 Demo.Widget::_state",
                        false)
                ],
                [sourceFile],
                [sourceLocation],
                [],
                [
                    new IndexRelationshipRecord(
                        "incoming-call",
                        snapshotId,
                        "method-caller",
                        MethodSymbolId,
                        null,
                        "Calls",
                        "fixture:incoming-call"),
                    new IndexRelationshipRecord(
                        "outgoing-call",
                        snapshotId,
                        MethodSymbolId,
                        "method-service-execute",
                        null,
                        "Calls",
                        "fixture:outgoing-call"),
                    new IndexRelationshipRecord(
                        "inherits-widget-base",
                        snapshotId,
                        "type-widget",
                        "type-base-widget",
                        null,
                        "Inherits",
                        "fixture:inherits"),
                    new IndexRelationshipRecord(
                        "parameter-type-payload",
                        snapshotId,
                        MethodSymbolId,
                        "type-payload",
                        null,
                        "ParameterType",
                        "fixture:parameter-type"),
                    new IndexRelationshipRecord(
                        "return-type-result",
                        snapshotId,
                        MethodSymbolId,
                        "type-result",
                        null,
                        "ReturnType",
                        "fixture:return-type"),
                    new IndexRelationshipRecord(
                        "reads-widget-field",
                        snapshotId,
                        MethodSymbolId,
                        "field-state",
                        null,
                        "ReadsField",
                        "fixture:reads-field")
                ]),
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

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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
