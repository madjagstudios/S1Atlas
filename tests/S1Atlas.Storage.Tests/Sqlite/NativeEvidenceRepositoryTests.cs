using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class NativeEvidenceRepositoryTests : IAsyncDisposable
{
    private const string GameAssemblySha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ToolSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OutputSha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ToolInstanceId = "tool-instance-native-evidence";
    private const string ProfileDigest = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string PolicyDigest = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-native-evidence-repository-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly SqliteAtlasRepository _repository;

    public NativeEvidenceRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsExactNativeRecoveryRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var expected = Record(
            recoveryId: new string('1', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T12:00:00Z"));

        await repository.SaveNativeRecoveryAsync(expected, cancellationToken);

        var byId = await repository.GetNativeRecoveryAsync(expected.RecoveryId, cancellationToken);
        AssertRecordEqual(expected, Assert.IsType<NativeRecoveryRecord>(byId));

        var matches = await repository.GetNativeRecoveriesAsync(expected.Request, cancellationToken);
        AssertRecordEqual(expected, Assert.Single(matches));
        Assert.Equal("UNKNOWN", matches[0].Edges[0].Kind);
        Assert.Equal("DirectCall", matches[0].Edges[1].Kind);
    }

    [Fact]
    public async Task GetNativeRecoveries_RequiresExactInputTupleAndOrdersDeterministically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var older = Record(
            recoveryId: new string('2', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        var newerA = Record(
            recoveryId: new string('3', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T13:00:00Z"),
            outputSha256: new string('d', 64));
        var newerB = Record(
            recoveryId: new string('4', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T13:00:00Z"),
            outputSha256: new string('e', 64));
        foreach (var record in new[] { newerB, older, newerA })
            await repository.SaveNativeRecoveryAsync(record, cancellationToken);

        var exact = await repository.GetNativeRecoveriesAsync(older.Request, cancellationToken);
        Assert.Equal(
            [newerA.RecoveryId, newerB.RecoveryId, older.RecoveryId],
            exact.Select(record => record.RecoveryId));
        var reorderedSet = older.Request with
        {
            SymbolIds = older.Request.SymbolIds.Reverse().ToArray()
        };
        Assert.Equal(
            exact.Select(record => record.RecoveryId),
            (await repository.GetNativeRecoveriesAsync(reorderedSet, cancellationToken))
                .Select(record => record.RecoveryId));

        var mismatches = new[]
        {
            older.Request with { BuildId = "build-b" },
            older.Request with { IndexId = "index-b" },
            older.Request with { GameAssemblySha256 = new string('f', 64) },
            older.Request with { SymbolIds = ["native-symbol-a", "native-symbol-c"] },
            older.Request with { MaxTraversalEdges = older.Request.MaxTraversalEdges + 1 }
        };
        foreach (var mismatch in mismatches)
            Assert.Empty(await repository.GetNativeRecoveriesAsync(mismatch, cancellationToken));
    }

    [Fact]
    public async Task GetNativeRecovery_RevalidatesPersistedEvidenceBeforeReturningIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var expected = Record(new string('7', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        await repository.SaveNativeRecoveryAsync(expected, cancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE native_recovery_runs SET output_sha256 = $outputSha256 WHERE recovery_id = $recoveryId;";
            command.Parameters.AddWithValue("$outputSha256", new string('d', 64));
            command.Parameters.AddWithValue("$recoveryId", expected.RecoveryId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetNativeRecoveryAsync(expected.RecoveryId, cancellationToken));
    }

    [Fact]
    public async Task SaveNativeRecovery_RejectsArtifactBearingSummaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var unsafeRecord = Record(
            recoveryId: new string('5', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
        {
            MappingEvidence = [@"C:\game\GameAssembly.dll disassembly"]
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.SaveNativeRecoveryAsync(unsafeRecord, cancellationToken));
        Assert.Null(await repository.GetNativeRecoveryAsync(unsafeRecord.RecoveryId, cancellationToken));
    }

    [Fact]
    public async Task SaveNativeRecovery_RejectsRawInstructionSummaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var unsafeRecords = new[]
        {
            Record(new string('a', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["  mov eax, ebx  "]
            },
            Record(new string('b', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["48 8B 05 12 34"]
            },
            Record(new string('e', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["0x1000: mov eax, ebx"]
            },
            Record(new string('f', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["00401000 55 push ebp"]
            },
            Record(new string('0', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["0x1000: 55 mov eax, ebx"]
            },
            Record(new string('4', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["00401000 55 8B EC push ebp"]
            }
        };

        foreach (var unsafeRecord in unsafeRecords)
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => repository.SaveNativeRecoveryAsync(unsafeRecord, cancellationToken));
            Assert.Null(await repository.GetNativeRecoveryAsync(unsafeRecord.RecoveryId, cancellationToken));
        }
    }

    [Fact]
    public async Task SaveNativeRecovery_NormalizesWhitespaceAroundSafeSummaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var record = Record(new string('c', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
        {
            MappingEvidence = ["  managed pointer 0x100  ", " native pointer 0x200 "]
        };

        await repository.SaveNativeRecoveryAsync(record, cancellationToken);

        var stored = Assert.IsType<NativeRecoveryRecord>(
            await repository.GetNativeRecoveryAsync(record.RecoveryId, cancellationToken));
        Assert.Equal(["managed pointer 0x100", "native pointer 0x200"], stored.MappingEvidence);
    }

    [Fact]
    public async Task SaveNativeRecovery_RejectsRecordsThatBreakNativeEvidenceInvariants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;
        var invalidRecords = new[]
        {
            Record(new string('d', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                Status = NativeRecoveryStatus.Unsupported,
                IsComplete = true
            },
            Record(new string('e', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                Status = NativeRecoveryStatus.Failed,
                MappingEvidence = ["managed pointer 0x100"],
                Edges = [],
                FieldAccesses = []
            },
            Record(new string('f', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                MappingEvidence = ["managed pointer 0x100"],
                Edges = [],
                FieldAccesses = []
            },
            Record(new string('1', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                Edges =
                [
                    new NativeEvidenceEdge(
                        new string('9', 64), "0x200", null, "Demo.Target", "DirectCall", "evidence", true)
                ],
                FieldAccesses = []
            },
            Record(new string('2', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                Edges =
                [
                    new NativeEvidenceEdge(
                        new string('9', 64), "0x200", null, "runtime target", "UNKNOWN", "UNKNOWN dispatch", true)
                ],
                FieldAccesses = []
            },
            Record(new string('3', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z")) with
            {
                Edges =
                [
                    new NativeEvidenceEdge(
                        new string('9', 64), "0x200", "0x220", "Demo.Target", "Indirect", "evidence", true)
                ],
                FieldAccesses = []
            }
        };

        foreach (var invalidRecord in invalidRecords)
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => repository.SaveNativeRecoveryAsync(invalidRecord, cancellationToken));
            Assert.Null(await repository.GetNativeRecoveryAsync(invalidRecord.RecoveryId, cancellationToken));
        }
    }

    [Fact]
    public async Task SaveNativeRecovery_LinksThroughValidatedExtractionsAndRejectsAnUnlinkedIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> repository = _repository;

        // Sanity check on the fixture's shape: a real Schedule I/Installed code_snapshot
        // always leaves environment_snapshot_id NULL, so this row must not carry one. If it
        // did, SaveNativeRecoveryAsync succeeding below would prove nothing about the
        // validated_extractions linkage -- it could just as well be passing through the old,
        // incorrect environment_snapshots join.
        await using (var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT environment_snapshot_id FROM code_snapshots WHERE snapshot_id = 'native-snapshot';";
            Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(cancellationToken));
        }

        var linked = Record(new string('2', 64), DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        await repository.SaveNativeRecoveryAsync(linked, cancellationToken);
        Assert.NotNull(await repository.GetNativeRecoveryAsync(linked.RecoveryId, cancellationToken));

        // A second completed index whose code_snapshot.source_identity does not resolve to
        // any validated_extractions row -- the real-world "no matching completed extraction"
        // case -- must still be rejected by the precondition.
        var orphanSnapshot = new CodeSnapshotRecord(
            "native-snapshot-orphan",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "orphan-source-identity-with-no-validated-extraction",
            "2026-08-30T10:05:00.0000000+00:00");
        await _repository.CreateCodeSnapshotAsync(orphanSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-a-orphan",
                orphanSnapshot.SnapshotId,
                IndexRunStatus.Running,
                "2026-08-30T10:05:00.0000000+00:00"),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-a-orphan",
            new IndexWriteSet([], [], [], [], []),
            "2026-08-30T10:06:00.0000000+00:00",
            cancellationToken);

        var unlinkedRequest = Request() with { IndexId = "index-a-orphan" };
        var unlinked = CreateCanonicalRecord(
            unlinkedRequest,
            "native-recovery-tool",
            "1.2.3",
            ToolSha256,
            NativeRecoveryStatus.Recovered,
            ["managed pointer 0x100", "native pointer 0x200"],
            [
                new NativeEvidenceEdge(
                    new string('8', 64), "0x200", null, "runtime target", "UNKNOWN", "UNKNOWN indirect dispatch", false),
                new NativeEvidenceEdge(
                    new string('7', 64), "0x200", "0x220", "Demo.Target", "DirectCall", "direct target evidence", true)
            ],
            ["0x300 read", "0x320 write"],
            false,
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveNativeRecoveryAsync(unlinked, cancellationToken));
        Assert.Equal(
            "Native recovery records require a completed Schedule I index matching the recorded build and GameAssembly hash.",
            exception.Message);
        Assert.Null(await repository.GetNativeRecoveryAsync(unlinked.RecoveryId, cancellationToken));
    }

    [Fact]
    public async Task ReadOnlyRepository_QueriesWithoutChangingAtlasAndRejectsWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await InitializeCompletedIndexAsync(cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> writable = _repository;
        var expected = Record(
            recoveryId: new string('6', 64),
            createdAtUtc: DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        await writable.SaveNativeRecoveryAsync(expected, cancellationToken);

        var before = await HashTreeAsync(_root, cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> readOnly =
            new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath));

        AssertRecordEqual(
            expected,
            Assert.Single(await readOnly.GetNativeRecoveriesAsync(expected.Request, cancellationToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readOnly.SaveNativeRecoveryAsync(expected, cancellationToken));
        var after = await HashTreeAsync(_root, cancellationToken);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ReadOnlyRepository_DoesNotMigrateV11Database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var legacyPath = Path.Combine(_root, "legacy-v11.db");
        await new SqliteMigrationRunner(
            legacyPath,
            Path.Combine(_root, "legacy-backups"),
            SqliteMigrations.All.Take(11).ToArray())
            .MigrateAsync(cancellationToken);
        var before = await HashTreeAsync(_root, cancellationToken);
        INativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest> readOnly =
            new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(legacyPath));

        await Assert.ThrowsAsync<SqliteException>(
            () => readOnly.GetNativeRecoveriesAsync(Request(), cancellationToken));

        Assert.Equal(before, await HashTreeAsync(_root, cancellationToken));
        await using var connection = new SqliteConnection($"Data Source={legacyPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(11L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
    }

    // Real Schedule I/Installed code_snapshots always leave environment_snapshot_id NULL
    // (only API-codebase snapshots set it); the index-to-build linkage instead flows
    // through code_snapshots.source_identity -> validated_extractions.extraction_id ->
    // builds.build_id. This fixture deliberately mirrors that real shape (rather than
    // setting environment_snapshot_id, which would let the precondition query pass through
    // the old, incorrect environment_snapshots join) so these tests exercise the same
    // linkage RequireCompletedNativeInputAsync relies on in production.
    private async Task InitializeCompletedIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var timestamp = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        var installationRoot = Path.GetFullPath(@"C:\games\native-build-a");
        var environment = new EnvironmentSnapshot(
            2,
            new GameBuild(
                "build-a",
                GameAssemblySha256,
                new string('9', 64),
                timestamp,
                true),
            new InstallationObservation(
                "2022.3",
                "3164500",
                "456",
                installationRoot,
                Path.Combine(installationRoot, "GameAssembly.dll"),
                Path.Combine(installationRoot, "global-metadata.dat")),
            [],
            "0.2.0-test",
            timestamp);
        await _repository.SaveSnapshotAsync(environment, cancellationToken);

        var extractionId = await SeedValidatedExtractionAsync("build-a", cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "native-snapshot",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            extractionId,
            "2026-08-30T10:00:00.0000000+00:00");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-a",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                "2026-08-30T10:00:00.0000000+00:00"),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-a",
            new IndexWriteSet([], [], [], [], []),
            "2026-08-30T10:01:00.0000000+00:00",
            cancellationToken);
    }

    private async Task<string> SeedValidatedExtractionAsync(string buildId, CancellationToken cancellationToken)
    {
        await SeedToolInstanceAsync(cancellationToken);
        var baseTime = DateTimeOffset.Parse("2026-08-30T09:00:00Z");
        var recipeId = new string('1', 64);
        var manifest = new ArtifactManifest(1, [
            new ArtifactManifestEntry(
                "reconstructed/Assembly-CSharp.dll",
                ArtifactKind.ManagedAssembly,
                6,
                Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5, 6])).ToLowerInvariant(),
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
        var attempt = await CreateValidatingAttemptAsync(buildId, recipeId, extractionId[..32], baseTime, cancellationToken);
        var statistics = new ExtractionStatistics(
            1, 1, 1, 1, 1, 0, 0, 0, 6, 6,
            [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 6, 1, 1, 0, 0, 0)]);
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
            Path.Combine(_root, "builds", buildId, "extractions", extractionId),
            baseTime.AddMinutes(1),
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
            baseTime.AddMinutes(2));
        await _repository.CommitValidatedExtractionAsync(
            new ValidatedExtractionPromotion(
                attempt with
                {
                    Status = ExtractionAttemptStatus.Succeeded,
                    CompletedAtUtc = baseTime.AddMinutes(2),
                    ResultExtractionId = extractionId
                },
                extraction,
                manifest,
                report,
                null),
            cancellationToken);
        return extractionId;
    }

    private async Task<ExtractionAttempt> CreateValidatingAttemptAsync(
        string buildId,
        string recipeId,
        string attemptId,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
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
            baseTime,
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
        await _repository.CreateAttemptAsync(created, cancellationToken);
        var preparing = created with { Status = ExtractionAttemptStatus.Preparing, StartedAtUtc = baseTime };
        await _repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, cancellationToken);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 1234 };
        await _repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, cancellationToken);
        var completed = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = "C:\\candidate"
        };
        await _repository.TransitionAttemptAsync(completed, ExtractionAttemptStatus.Running, cancellationToken);
        var validating = completed with { Status = ExtractionAttemptStatus.Validating };
        await _repository.TransitionAttemptAsync(validating, ExtractionAttemptStatus.ProcessCompleted, cancellationToken);
        return validating;
    }

    private async Task SeedToolInstanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
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
                $id, 'cpp2il', 'test', 'win-x64', 'ManagedPinned', 'definition',
                'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-30T09:00:00.0000000+00:00', '2026-08-30T09:00:00.0000000+00:00', 'Verified');
            """;
        command.Parameters.AddWithValue("$id", ToolInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NativeRecoveryRequest Request() =>
        new(
            "build-a",
            "index-a",
            GameAssemblySha256,
            ["native-symbol-a", "native-symbol-b"],
            25);

    private static NativeRecoveryRecord Record(
        string recoveryId,
        DateTimeOffset createdAtUtc,
        string outputSha256 = OutputSha256) =>
        CreateCanonicalRecord(
            Request(),
            "native-recovery-tool",
            "1.2.3",
            ToolSha256,
            NativeRecoveryStatus.Recovered,
            outputSha256 == OutputSha256
                ? ["managed pointer 0x100", "native pointer 0x200"]
                : ["managed pointer 0x100", "native pointer 0x200", "record variant " + outputSha256[..1]],
            [
                new NativeEvidenceEdge(
                    new string('8', 64),
                    "0x200",
                    null,
                    "runtime target",
                    "UNKNOWN",
                    "UNKNOWN indirect dispatch",
                    false),
                new NativeEvidenceEdge(
                    new string('7', 64),
                    "0x200",
                    "0x220",
                    "Demo.Target",
                    "DirectCall",
                    "direct target evidence",
                    true)
            ],
            ["0x300 read", "0x320 write"],
            false,
            createdAtUtc,
            null);

    private static NativeRecoveryRecord CreateCanonicalRecord(
        NativeRecoveryRequest request,
        string toolName,
        string toolVersion,
        string toolSha256,
        NativeRecoveryStatus status,
        IReadOnlyList<string> mappingEvidence,
        IReadOnlyList<NativeEvidenceEdge> edges,
        IReadOnlyList<string> fieldAccesses,
        bool isComplete,
        DateTimeOffset createdAtUtc,
        string? failureMessage)
    {
        var outputSha256 = NativeRecoveryIntegrity.ComputeOutputSha256(
            status,
            mappingEvidence,
            edges,
            fieldAccesses,
            isComplete,
            failureMessage);
        return new NativeRecoveryRecord(
            NativeRecoveryIntegrity.ComputeRecoveryId(
                request,
                toolName,
                toolVersion,
                toolSha256,
                outputSha256),
            request,
            toolName,
            toolVersion,
            toolSha256,
            status,
            mappingEvidence,
            edges,
            fieldAccesses,
            isComplete,
            outputSha256,
            createdAtUtc,
            failureMessage);
    }

    private static void AssertRecordEqual(
        NativeRecoveryRecord expected,
        NativeRecoveryRecord actual)
    {
        Assert.Equal(expected.RecoveryId, actual.RecoveryId);
        Assert.Equal(expected.Request.BuildId, actual.Request.BuildId);
        Assert.Equal(expected.Request.IndexId, actual.Request.IndexId);
        Assert.Equal(expected.Request.GameAssemblySha256, actual.Request.GameAssemblySha256);
        Assert.Equal(expected.Request.SymbolIds, actual.Request.SymbolIds);
        Assert.Equal(expected.Request.MaxTraversalEdges, actual.Request.MaxTraversalEdges);
        Assert.Equal(expected.ToolName, actual.ToolName);
        Assert.Equal(expected.ToolVersion, actual.ToolVersion);
        Assert.Equal(expected.ToolSha256, actual.ToolSha256);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.MappingEvidence, actual.MappingEvidence);
        Assert.Equal(expected.Edges, actual.Edges);
        Assert.Equal(expected.FieldAccesses, actual.FieldAccesses);
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        Assert.Equal(expected.OutputSha256, actual.OutputSha256);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.FailureMessage, actual.FailureMessage);
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            var relativePath = Path.GetRelativePath(root, path);
            var length = new FileInfo(path).Length;
            hashes.Add(relativePath, $"{length}:{hash}");
        }

        return hashes;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
