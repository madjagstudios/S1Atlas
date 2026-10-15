using S1Atlas.Cli;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Upstream;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class IndexCliTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-index-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Index_json_is_registered_and_fails_closed_without_authority()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["index", "--json"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("NoEnvironmentSnapshot", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Interop_path_rejects_api_indexing_options()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--interop-path", "interop.dll", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("InvalidOptionCombination", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_build_targets_that_build_without_a_current_snapshot()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--build", new string('a', 64), "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.DoesNotContain("InvalidOptionCombination", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("NoEnvironmentSnapshot", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("No preferred integrity-verified extraction is available.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_installed_s1api_uses_the_current_snapshot_dependency_and_persists_provenance()
    {
        var assemblyPath = Path.Combine(_dataDirectory, "inputs", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var snapshot = await SaveSnapshotAsync(
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true));
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("S1Api", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Installed", output.ToString(), StringComparison.Ordinal);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var environmentSnapshotId = EnvironmentSnapshotId.Create(snapshot);
        var completed = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            environmentSnapshotId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.NotEmpty(await repository.GetCompletedSymbolsAsync(completed!.IndexId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Index_installed_api_returns_stable_missing_dependency_error_without_substitution()
    {
        await SaveSnapshotAsync(new DependencyVersion(DependencyKind.S1Api, null, null, false));
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("InstalledDependencyMissing", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_cached_release_uses_only_the_existing_cache_and_reports_the_commit_identity()
    {
        var commitSha = new string('f', 40);
        var source = Encoding.UTF8.GetBytes("namespace Demo; public sealed class Cached { public void Run() { } }");
        await new UpstreamSnapshotCache(_dataDirectory).SaveAsync(
            new UpstreamSnapshot(
                new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo"),
                commitSha,
                [new UpstreamFile("src/Cached.cs", source, Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant())]),
            TestContext.Current.CancellationToken);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "release", "--commit", commitSha, "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(commitSha, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Release", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_default_builds_installed_s1api_alongside_the_game_index()
    {
        var assemblyPath = Path.Combine(_dataDirectory, "inputs", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var snapshot = await SaveSnapshotAsync(
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true));
        await SeedRunnableGameExtractionAsync();
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("ScheduleI", data.GetProperty("codebase").GetString());
        var apiIndexes = data.GetProperty("apiIndexes");
        Assert.Equal(1, apiIndexes.GetArrayLength());
        var api = apiIndexes[0];
        Assert.Equal("S1Api", api.GetProperty("codebase").GetString());
        Assert.Equal("Installed", api.GetProperty("channel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(api.GetProperty("indexId").GetString()));
        Assert.True(api.GetProperty("symbolCount").GetInt32() > 0);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var environmentSnapshotId = EnvironmentSnapshotId.Create(snapshot);
        var completed = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            environmentSnapshotId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(api.GetProperty("indexId").GetString(), completed!.IndexId);
        Assert.NotEmpty(await repository.GetCompletedSymbolsAsync(completed.IndexId, TestContext.Current.CancellationToken));
        Assert.NotNull(await repository.GetCompletedIndexAsync(
            data.GetProperty("indexId").GetString()!,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Index_default_without_api_dependencies_succeeds_with_empty_api_indexes()
    {
        await SaveSnapshotAsync(new DependencyVersion(DependencyKind.S1Api, null, null, false));
        await SeedRunnableGameExtractionAsync();
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("ScheduleI", data.GetProperty("codebase").GetString());
        Assert.Empty(data.GetProperty("apiIndexes").EnumerateArray());

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        Assert.NotNull(await repository.GetCompletedIndexAsync(
            data.GetProperty("indexId").GetString()!,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Index_default_with_missing_api_dll_warns_and_keeps_game_index()
    {
        var missingPath = Path.Combine(_dataDirectory, "inputs", "S1API.dll");
        await SaveSnapshotAsync(
            new DependencyVersion(DependencyKind.S1Api, "fixture", missingPath, true));
        await SeedRunnableGameExtractionAsync();
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("ScheduleI", data.GetProperty("codebase").GetString());
        Assert.Empty(data.GetProperty("apiIndexes").EnumerateArray());
        Assert.Contains(
            data.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("S1Api", StringComparison.Ordinal));

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        Assert.NotNull(await repository.GetCompletedIndexAsync(
            data.GetProperty("indexId").GetString()!,
            TestContext.Current.CancellationToken));
    }

    private async Task SeedRunnableGameExtractionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildId = new string('a', 64);
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        const string toolInstanceId = "tool-index-cli";
        await ExtractionSeed.SeedToolInstanceAsync(_dataDirectory, toolInstanceId, cancellationToken);

        var assemblyBytes = await File.ReadAllBytesAsync(
            typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location,
            cancellationToken);
        var assemblySha256 = Convert.ToHexString(SHA256.HashData(assemblyBytes)).ToLowerInvariant();
        var manifest = new ArtifactManifest(
            1,
            [
                new ArtifactManifestEntry(
                    "reconstructed/Assembly-CSharp.dll",
                    ArtifactKind.ManagedAssembly,
                    assemblyBytes.Length,
                    assemblySha256,
                    "Assembly-CSharp",
                    "Assembly-CSharp.dll",
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var recipeId = ExtractionSeed.Sha256("recipe-index-cli");
        var extractionId = ExtractionId.Create(recipeId, digest);
        var baseTime = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var profileDigest = ExtractionSeed.Sha256("profile-index-cli");
        var policyDigest = ExtractionSeed.Sha256("policy-index-cli");
        var inputSnapshot = InputSnapshot.CreateUnverified(
            buildId,
            Path.Combine(_dataDirectory, "inputs"),
            new InputManifest([]),
            baseTime);
        await repository.SaveInputSnapshotAsync(inputSnapshot, cancellationToken);
        await repository.MarkInputSnapshotReplayVerifiedAsync(
            inputSnapshot.InputSnapshotId,
            buildId,
            inputSnapshot.ManifestDigest,
            baseTime.AddMinutes(1),
            cancellationToken);
        var attempt = await ExtractionSeed.AdvanceAttemptToValidatingAsync(
            repository,
            buildId,
            recipeId,
            extractionId[..32],
            inputSnapshot.InputSnapshotId,
            toolInstanceId,
            profileDigest,
            policyDigest,
            baseTime,
            cancellationToken);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0,
            TotalOutputBytes: assemblyBytes.Length,
            TotalManagedBytes: assemblyBytes.Length,
            Assemblies:
            [
                new AssemblyIdentityStatistics(
                    "Assembly-CSharp",
                    1,
                    assemblyBytes.Length,
                    5,
                    10,
                    2,
                    1,
                    0)
            ]);
        var documentsRoot = Path.Combine(_dataDirectory, "builds", buildId, "extractions", extractionId);
        var extraction = new ValidatedExtraction(
            extractionId,
            recipeId,
            buildId,
            toolInstanceId,
            attempt.AttemptId,
            "default-profile",
            1,
            profileDigest,
            1,
            1,
            digest,
            documentsRoot,
            baseTime.AddMinutes(10),
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
            policyDigest,
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
            baseTime.AddMinutes(11));
        var promotion = new ValidatedExtractionPromotion(
            attempt with
            {
                Status = ExtractionAttemptStatus.Succeeded,
                CompletedAtUtc = baseTime.AddMinutes(11),
                ResultExtractionId = extractionId
            },
            extraction,
            manifest,
            report,
            AutomaticPreferenceReason: null);

        Directory.CreateDirectory(Path.Combine(documentsRoot, "reconstructed"));
        await File.WriteAllBytesAsync(
            Path.Combine(documentsRoot, "reconstructed", "Assembly-CSharp.dll"),
            assemblyBytes,
            cancellationToken);
        await WriteValidatedExtractionDocumentsAsync(
            _dataDirectory,
            documentsRoot,
            extraction,
            manifest,
            report,
            cancellationToken);
        await repository.CommitValidatedExtractionAsync(promotion, cancellationToken);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                buildId,
                extractionId,
                report.ValidatedAtUtc,
                ExtractionPreferenceReason.ManualPromotion),
            cancellationToken);
    }

    private static async Task WriteValidatedExtractionDocumentsAsync(
        string dataRoot,
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
            [dataRoot, documentsRoot, extraction, manifest, report, cancellationToken])!;
        await writeTask;
    }

    private async Task<EnvironmentSnapshot> SaveSnapshotAsync(DependencyVersion apiDependency)
    {
        var snapshot = new EnvironmentSnapshot(
            2,
            new GameBuild(
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                DateTimeOffset.UtcNow,
                true),
            new InstallationObservation("2022.3.62", "3164500", "fixture", _dataDirectory, null, null),
            [
                apiDependency,
                new DependencyVersion(DependencyKind.S1Mapi, null, null, false),
                new DependencyVersion(DependencyKind.MelonLoader, null, null, false),
                new DependencyVersion(DependencyKind.Sideload, null, null, false)
            ],
            "0.1.0-test",
            DateTimeOffset.UtcNow);
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        await repository.SaveSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }
}
