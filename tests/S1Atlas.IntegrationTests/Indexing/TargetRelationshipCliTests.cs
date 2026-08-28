using System.Security.Cryptography;
using System.Text.Json;
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

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class TargetRelationshipCliTests
{
    [Fact]
    public async Task Callsites_scope_all_returns_bounded_deterministic_json_with_unresolved_target_text()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run(
            "callsites",
            "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink",
            "--scope",
            "all",
            "--collection",
            atlas.Collection,
            "--limit",
            "2",
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, data.GetProperty("returnedCount").GetInt32());
        var relationships = data.GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(
            ["call-001-game", "call-002-reference"],
            relationships.Select(item => item.GetProperty("relationshipId").GetString()!).ToArray());
        Assert.Equal("game", relationships[0].GetProperty("source").GetProperty("origin").GetString());
        Assert.Equal("reference", relationships[1].GetProperty("source").GetProperty("origin").GetString());
        Assert.Equal(atlas.Collection, relationships[1].GetProperty("source").GetProperty("collection").GetString());
        Assert.Equal("qol", relationships[1].GetProperty("source").GetProperty("referenceModId").GetString());
        Assert.Equal(
            "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()",
            relationships[1].GetProperty("target").GetProperty("rawText").GetString());
    }

    [Fact]
    public async Task FieldRefs_filters_readers_and_writers_and_rejects_mutually_exclusive_flags()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var readers = atlas.Run(
            "fieldrefs",
            "Demo.State.Value",
            "--scope",
            "all",
            "--collection",
            atlas.Collection,
            "--readers",
            "--json");
        var writers = atlas.Run(
            "fieldrefs",
            "Demo.State.Value",
            "--scope",
            "all",
            "--collection",
            atlas.Collection,
            "--writers",
            "--json");
        var invalid = atlas.Run(
            "fieldrefs",
            "Demo.State.Value",
            "--scope",
            "all",
            "--collection",
            atlas.Collection,
            "--readers",
            "--writers",
            "--json");

        Assert.Equal(0, readers.ExitCode);
        Assert.Equal(0, writers.ExitCode);
        Assert.Equal(1, invalid.ExitCode);

        using var readersJson = JsonDocument.Parse(readers.StandardOutput);
        using var writersJson = JsonDocument.Parse(writers.StandardOutput);
        using var invalidJson = JsonDocument.Parse(invalid.StandardOutput);

        var readerRelationships = readersJson.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(
            ["field-001-game-read", "field-002-reference-read"],
            readerRelationships.Select(item => item.GetProperty("relationshipId").GetString()!).ToArray());
        Assert.Equal("reference", readerRelationships[1].GetProperty("source").GetProperty("origin").GetString());
        Assert.Equal("game", readerRelationships[1].GetProperty("target").GetProperty("origin").GetString());

        var writerRelationships = writersJson.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(
            ["field-003-game-write"],
            writerRelationships.Select(item => item.GetProperty("relationshipId").GetString()!).ToArray());
        Assert.Contains(
            "recovered IL references",
            readersJson.RootElement.GetProperty("data").GetProperty("completenessNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "lifecycle ordering",
            readersJson.RootElement.GetProperty("data").GetProperty("completenessNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            "InvalidOptionCombination",
            invalidJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

internal sealed class TargetRelationshipCliAtlas : IAsyncDisposable
{
    private const string BuildId = "build-current";
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private TargetRelationshipCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }
    public string Collection => "qol";

    public static async Task<TargetRelationshipCliAtlas> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-target-query-cli-" + Guid.NewGuid().ToString("N"));
        var atlas = new TargetRelationshipCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(CancellationToken.None);
        await atlas.SeedToolInstanceAsync();
        await atlas.SeedSnapshotAsync(BuildId);
        var extractionId = await atlas.SeedValidatedExtractionAsync(BuildId);
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(BuildId, extractionId, BaseTime.AddMinutes(2), ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
        var gameRun = await atlas.SeedGameIndexAsync(extractionId);
        await atlas.SeedReferenceIndexAsync(gameRun.IndexId);
        return atlas;
    }

    public (int ExitCode, string StandardOutput, string StandardError) Run(params string[] args)
    {
        var application = new CliApplication(DataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private async Task<string> SeedValidatedExtractionAsync(string buildId)
    {
        const string recipeId = "1111111111111111111111111111111111111111111111111111111111111111";
        var manifest = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/Assembly-CSharp.dll",
                ArtifactKind.ManagedAssembly,
                6,
                Convert.ToHexString(SHA256.HashData([10, 20, 30, 40, 50, 60])).ToLowerInvariant(),
                "Assembly-CSharp",
                "Assembly-CSharp.dll",
                1,
                1,
                0,
                0,
                0)
        ]);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var attempt = await CreateValidatingAttemptAsync(buildId, recipeId, extractionId[..32]);
        var statistics = new ExtractionStatistics(
            1,
            1,
            1,
            1,
            1,
            0,
            0,
            0,
            6,
            6,
            [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 6, 1, 1, 0, 0, 0)]);
        var extractionRoot = Path.Combine(DataRoot, "builds", buildId, "extractions", extractionId);
        var extraction = new ValidatedExtraction(
            extractionId,
            recipeId,
            buildId,
            ToolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            ProfileDigest,
            1,
            1,
            digest,
            extractionRoot,
            BaseTime.AddMinutes(1),
            ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid,
            statistics);
        var report = new ValidationReport(
            1,
            attempt.AttemptId,
            ValidationSubjectKind.CandidateOutput,
            null,
            buildId,
            recipeId,
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
            BaseTime.AddMinutes(2));
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        await File.WriteAllBytesAsync(
            Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"),
            [10, 20, 30, 40, 50, 60]);
        await WriteValidatedExtractionDocumentsAsync(extractionRoot, extraction, manifest, report);
        await _repository.CommitValidatedExtractionAsync(
            new ValidatedExtractionPromotion(
                attempt with
                {
                    Status = ExtractionAttemptStatus.Succeeded,
                    CompletedAtUtc = BaseTime.AddMinutes(2),
                    ResultExtractionId = extractionId
                },
                extraction,
                manifest,
                report,
                null),
            CancellationToken.None);
        return extractionId;
    }

    private async Task<IndexRunRecord> SeedGameIndexAsync(string extractionId)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-game",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            BaseTime.AddMinutes(3).ToString("O"));
        await _repository.CreateCodeSnapshotAsync(snapshot, CancellationToken.None);
        var run = new IndexRunRecord("index-game", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(run, CancellationToken.None);

        var callSourceA = Method("game-call-source-a", snapshot.SnapshotId, "Demo.CallerA.Run");
        var callSourceB = Method("game-call-source-b", snapshot.SnapshotId, "Demo.CallerB.Run");
        var fieldReader = Method("game-field-reader", snapshot.SnapshotId, "Demo.FieldReader.Read");
        var fieldWriter = Method("game-field-writer", snapshot.SnapshotId, "Demo.FieldWriter.Write");
        var field = Field("game-field", snapshot.SnapshotId, "Demo.State.Value");

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [callSourceA, callSourceB, fieldReader, fieldWriter, field],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord("call-001-game", snapshot.SnapshotId, callSourceA.SymbolId, null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Calls", "fixture:game"),
                    new IndexRelationshipRecord("call-003-game", snapshot.SnapshotId, callSourceB.SymbolId, null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Calls", "fixture:game"),
                    new IndexRelationshipRecord("field-001-game-read", snapshot.SnapshotId, fieldReader.SymbolId, field.SymbolId, "Demo.State::Value", "ReadsField", "fixture:game"),
                    new IndexRelationshipRecord("field-003-game-write", snapshot.SnapshotId, fieldWriter.SymbolId, field.SymbolId, "Demo.State::Value", "WritesField", "fixture:game")
                ]),
            BaseTime.AddMinutes(4).ToString("O"),
            CancellationToken.None);
        return run with { Status = IndexRunStatus.Completed, CompletedAtUtc = BaseTime.AddMinutes(4).ToString("O") };
    }

    private async Task SeedReferenceIndexAsync(string gameIndexId)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-reference",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            Collection,
            BaseTime.AddMinutes(5).ToString("O"));
        await _repository.CreateCodeSnapshotAsync(snapshot, CancellationToken.None);
        var run = new IndexRunRecord("index-reference", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await _repository.StartIndexRunAsync(run, CancellationToken.None);

        var callSource = Method("reference-call-source", snapshot.SnapshotId, "qol/Qol.Caller.Run");
        var fieldReader = Method("reference-field-reader", snapshot.SnapshotId, "qol/Qol.FieldReader.Read");
        var fieldWriter = Method("reference-field-writer", snapshot.SnapshotId, "qol/Qol.FieldWriter.Write");
        var referenceField = Field("reference-field", snapshot.SnapshotId, "qol/Qol.Config.Setting");
        var symbols = new[] { callSource, fieldReader, fieldWriter, referenceField };

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord("call-002-reference", snapshot.SnapshotId, callSource.SymbolId, null, "UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()", "Calls", "fixture:reference"),
                    new IndexRelationshipRecord("field-002-reference-read", snapshot.SnapshotId, fieldReader.SymbolId, "game-field", "Demo.State::Value", "ReadsField", "fixture:reference"),
                    new IndexRelationshipRecord("field-004-reference-write", snapshot.SnapshotId, fieldWriter.SymbolId, referenceField.SymbolId, "qol/Qol.Config::Setting", "WritesField", "fixture:reference")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, BuildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        "mods/qol",
                        "qol-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            BaseTime.AddMinutes(6).ToString("O"),
            CancellationToken.None);
    }

    private async Task<ExtractionAttempt> CreateValidatingAttemptAsync(string buildId, string recipeId, string attemptId)
    {
        var created = new ExtractionAttempt(
            attemptId,
            recipeId,
            buildId,
            ToolInstanceId,
            "default-profile",
            1,
            ProfileDigest,
            "managed-assemblies-v1",
            1,
            PolicyDigest,
            1,
            1,
            ExtractionInputSource.Live,
            null,
            ExtractionAttemptStatus.Created,
            BaseTime,
            null,
            null,
            null,
            null,
            $"C:\\attempts\\{attemptId}\\work",
            $"C:\\attempts\\{attemptId}\\stdout.log",
            $"C:\\attempts\\{attemptId}\\stderr.log",
            false,
            false,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            false,
            0,
            0,
            null,
            null);
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

    private Task SeedSnapshotAsync(string buildId) =>
        _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, BaseTime, true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                BaseTime),
            CancellationToken.None);

    private async Task SeedToolInstanceAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DataRoot, "atlas.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO tool_instances (tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ($id, 'cpp2il', 'test', 'win-x64', 'ManagedPinned', 'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe', '2026-08-28T12:00:00.0000000+00:00', '2026-08-28T12:05:00.0000000+00:00', 'Verified');""";
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

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "Fixture:Installed:Method:" + CanonicalMember(qualifiedName),
            "Method",
            qualifiedName,
            "System.Void " + CanonicalMember(qualifiedName) + "()",
            false,
            BodyRecoveryStatus.Recovered);

    private static IndexSymbolRecord Field(string id, string snapshotId, string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        var typeName = qualifiedName[..separator];
        var fieldName = qualifiedName[(separator + 1)..];
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "Fixture:Installed:Field:" + typeName + "::System.Int32 " + fieldName,
            "Field",
            qualifiedName,
            "System.Int32 " + typeName + "::" + fieldName,
            false);
    }

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }
}
