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
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class DelegateAddressTakenCliTests
{
    private const string DelegateLabel = "delegate created (not called)";
    private const string PossibleWriteLabel = "possible write (address taken)";
    private const string PossibleReadLabel = "possible read (address taken)";

    [Fact]
    public async Task Callers_hides_delegates_by_default()
    {
        await using var atlas = await DelegateCliAtlas.CreateAsync();

        var result = atlas.Run("callers", "Demo.DelegateCli.Handle", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Empty(data.GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public async Task Callers_shows_labeled_delegates_with_flag()
    {
        await using var atlas = await DelegateCliAtlas.CreateAsync();

        var result = atlas.Run("callers", "Demo.DelegateCli.Handle", "--include-delegates", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var row = Assert.Single(document.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray());
        Assert.Equal("ReferencesMethod", row.GetProperty("kind").GetString());
        Assert.Equal(DelegateLabel, row.GetProperty("label").GetString());

        var human = atlas.Run("callers", "Demo.DelegateCli.Handle", "--include-delegates");

        Assert.Equal(0, human.ExitCode);
        Assert.Contains($"ReferencesMethod ({DelegateLabel})", human.StandardOutput);
    }

    [Fact]
    public async Task Callees_hides_delegates_by_default()
    {
        await using var atlas = await DelegateCliAtlas.CreateAsync();

        var result = atlas.Run("callees", "Demo.DelegateCli.Build", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Empty(data.GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public async Task Callees_shows_labeled_delegates_with_flag()
    {
        await using var atlas = await DelegateCliAtlas.CreateAsync();

        var result = atlas.Run("callees", "Demo.DelegateCli.Build", "--include-delegates", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var row = Assert.Single(document.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray());
        Assert.Equal("ReferencesMethod", row.GetProperty("kind").GetString());
        Assert.Equal(DelegateLabel, row.GetProperty("label").GetString());

        var human = atlas.Run("callees", "Demo.DelegateCli.Build", "--include-delegates");

        Assert.Equal(0, human.ExitCode);
        Assert.Contains($"ReferencesMethod ({DelegateLabel})", human.StandardOutput);
    }

    [Fact]
    public async Task Fieldrefs_renders_address_taken_labels()
    {
        await using var atlas = await DelegateCliAtlas.CreateAsync();

        var all = atlas.Run("field-refs", "Demo.DelegateCli.Count", "--json");

        Assert.Equal(0, all.ExitCode);
        using var allDocument = JsonDocument.Parse(all.StandardOutput);
        var allRows = allDocument.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(3, allRows.Length);
        var allTaker = Assert.Single(allRows, row => row.GetProperty("relationshipId").GetString() == "addr-001-game");
        Assert.Equal(PossibleWriteLabel, allTaker.GetProperty("label").GetString());

        var writers = atlas.Run("field-refs", "Demo.DelegateCli.Count", "--writers", "--json");

        Assert.Equal(0, writers.ExitCode);
        using var writersDocument = JsonDocument.Parse(writers.StandardOutput);
        var writerRows = writersDocument.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, writerRows.Length);
        var writeTaker = Assert.Single(writerRows, row => row.GetProperty("relationshipId").GetString() == "addr-001-game");
        Assert.Equal(PossibleWriteLabel, writeTaker.GetProperty("label").GetString());

        var readers = atlas.Run("field-refs", "Demo.DelegateCli.Count", "--readers", "--json");

        Assert.Equal(0, readers.ExitCode);
        using var readersDocument = JsonDocument.Parse(readers.StandardOutput);
        var readerRows = readersDocument.RootElement.GetProperty("data").GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, readerRows.Length);
        var readTaker = Assert.Single(readerRows, row => row.GetProperty("relationshipId").GetString() == "addr-001-game");
        Assert.Equal(PossibleReadLabel, readTaker.GetProperty("label").GetString());

        var human = atlas.Run("field-refs", "Demo.DelegateCli.Count");

        Assert.Equal(0, human.ExitCode);
        Assert.Contains($"TakesFieldAddress ({PossibleWriteLabel})", human.StandardOutput);
    }
}

internal sealed class DelegateCliAtlas : IAsyncDisposable
{
    private const string BuildId = "build-current";
    private const string ToolInstanceId = "tool-instance-1";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private readonly string _root;
    private readonly SqliteAtlasRepository _repository;

    private DelegateCliAtlas(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "atlas");
        _repository = new SqliteAtlasRepository(Path.Combine(DataRoot, "atlas.db"), Path.Combine(DataRoot, "backups"));
    }

    public string DataRoot { get; }

    public static async Task<DelegateCliAtlas> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-delegate-cli-" + Guid.NewGuid().ToString("N"));
        var atlas = new DelegateCliAtlas(root);
        Directory.CreateDirectory(atlas.DataRoot);
        await atlas._repository.InitializeAsync(CancellationToken.None);
        await atlas.SeedToolInstanceAsync();
        await atlas.SeedSnapshotAsync(BuildId);
        var extractionId = await atlas.SeedValidatedExtractionAsync(BuildId);
        await atlas._repository.SetPreferredExtractionAsync(
            new PreferredExtraction(BuildId, extractionId, BaseTime.AddMinutes(2), ExtractionPreferenceReason.ManualPromotion),
            CancellationToken.None);
        await atlas.SeedGameIndexAsync(extractionId);
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

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
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

    private async Task SeedGameIndexAsync(string extractionId)
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

        var creator = Method("game-creator", snapshot.SnapshotId, "Demo.DelegateCli.Build");
        var target = Method("game-target", snapshot.SnapshotId, "Demo.DelegateCli.Handle");
        var caller = Method("game-caller", snapshot.SnapshotId, "Demo.DelegateCli.Run");
        var leaf = Method("game-leaf", snapshot.SnapshotId, "Demo.DelegateCli.Help");
        var field = Field("game-field", snapshot.SnapshotId, "Demo.DelegateCli.Count");
        var reader = Method("game-reader", snapshot.SnapshotId, "Demo.DelegateCli.Read");
        var writer = Method("game-writer", snapshot.SnapshotId, "Demo.DelegateCli.Write");
        var taker = Method("game-taker", snapshot.SnapshotId, "Demo.DelegateCli.Take");

        await _repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [creator, target, caller, leaf, field, reader, writer, taker],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord("del-001-game", snapshot.SnapshotId, creator.SymbolId, target.SymbolId, "Demo.DelegateCli::Handle()", "ReferencesMethod", "RecoveredIL"),
                    new IndexRelationshipRecord("call-001-game", snapshot.SnapshotId, caller.SymbolId, leaf.SymbolId, "Demo.DelegateCli::Help()", "Calls", "RecoveredIL"),
                    new IndexRelationshipRecord("field-001-game-read", snapshot.SnapshotId, reader.SymbolId, field.SymbolId, "Demo.DelegateCli::Count", "ReadsField", "RecoveredIL"),
                    new IndexRelationshipRecord("field-002-game-write", snapshot.SnapshotId, writer.SymbolId, field.SymbolId, "Demo.DelegateCli::Count", "WritesField", "RecoveredIL"),
                    new IndexRelationshipRecord("addr-001-game", snapshot.SnapshotId, taker.SymbolId, field.SymbolId, "Demo.DelegateCli::Count", "TakesFieldAddress", "RecoveredIL")
                ]),
            BaseTime.AddMinutes(4).ToString("O"),
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
