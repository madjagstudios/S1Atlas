using S1Atlas.Application.Readiness;
using S1Atlas.Cli;
using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class InstalledApiAcceptanceTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-installed-api-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scan_extract_index_answers_s1api_queries_from_the_installed_dll_and_doctor_reports_ok()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildId = new string('a', 64);
        var assemblyPath = Path.Combine(_dataDirectory, "game", "Mods", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var dllHash = await Sha256OfFileAsync(assemblyPath, cancellationToken);
        await SaveSnapshotAsync(
            buildId,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true, dllHash));
        await SeedRunnableGameExtractionAsync(buildId, "main");
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var indexOutput = new StringWriter();
        using var indexError = new StringWriter();

        var indexExitCode = application.Invoke(["index"], indexOutput, indexError, cancellationToken);

        Assert.Equal(0, indexExitCode);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository, _dataDirectory));
        var catalog = await service.ListAsync(null, cancellationToken);
        var selection = S1ApiInstalledSelection(catalog);
        Assert.Equal(ApiIndexAvailability.Current, selection.Availability);
        Assert.Equal(dllHash, selection.SourceIdentity);

        using var searchOutput = new StringWriter();
        using var searchError = new StringWriter();
        var searchExitCode = application.Invoke(
            ["search", "FixtureRoot", "--codebase", "s1api", "--channel", "installed", "--json"],
            searchOutput,
            searchError,
            cancellationToken);
        Assert.Equal(0, searchExitCode);
        Assert.Contains("FixtureRoot", searchOutput.ToString(), StringComparison.Ordinal);

        using var typeOutput = new StringWriter();
        using var typeError = new StringWriter();
        var typeExitCode = application.Invoke(
            ["type", "S1Atlas.ManagedAssemblyFixture.FixtureRoot", "--codebase", "s1api", "--channel", "installed", "--json"],
            typeOutput,
            typeError,
            cancellationToken);
        Assert.Equal(0, typeExitCode);
        Assert.Contains(
            "S1Atlas.ManagedAssemblyFixture.FixtureRoot",
            typeOutput.ToString(),
            StringComparison.Ordinal);

        using var doctorOutput = new StringWriter();
        using var doctorError = new StringWriter();
        _ = application.Invoke(["doctor", "--json"], doctorOutput, doctorError, cancellationToken);
        Assert.Equal("ok", GetDoctorItemState(doctorOutput.ToString(), ReadinessItemIds.ApiIndex));
    }

    [Fact]
    public async Task Environment_only_rescan_explains_stale_api_index_and_doctor_until_reindex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildId = new string('a', 64);
        var assemblyPath = Path.Combine(_dataDirectory, "game", "Mods", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var dllHash = await Sha256OfFileAsync(assemblyPath, cancellationToken);
        var first = await SaveSnapshotAsync(
            buildId,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true, dllHash));
        await SeedRunnableGameExtractionAsync(buildId, "environment-only");
        var application = new CliApplication(_dataDirectory, "0.1.0-test");

        (int ExitCode, string Output, string Error) Run(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = application.Invoke(args, output, error, cancellationToken);
            return (exitCode, output.ToString(), error.ToString());
        }

        var initialIndex = Run("index");
        Assert.Equal(0, initialIndex.ExitCode);
        Assert.Equal("ok", GetDoctorItemState(Run("doctor", "--json").Output, ReadinessItemIds.ApiIndex));

        var second = first with
        {
            Dependencies = first.Dependencies.Select(dependency =>
                dependency.Kind == DependencyKind.MelonLoader
                    ? new DependencyVersion(
                        DependencyKind.MelonLoader, "fixture",
                        Path.Combine(_dataDirectory, "game", "MelonLoader", "MelonLoader.dll"), true)
                    : dependency).ToArray(),
            CapturedAtUtc = first.CapturedAtUtc.AddMinutes(1),
            SnapshotId = null
        };
        Assert.Equal(first.Build.BuildId, second.Build.BuildId);
        Assert.NotEqual(EnvironmentSnapshotId.Create(first), EnvironmentSnapshotId.Create(second));
        Assert.Equal(dllHash, await Sha256OfFileAsync(assemblyPath, cancellationToken));
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.SaveSnapshotAsync(second, cancellationToken);

        var query = Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api", "--json");
        Assert.Equal(1, query.ExitCode);
        using var queryJson = JsonDocument.Parse(query.Output);
        var queryError = queryJson.RootElement.GetProperty("error");
        Assert.Equal("StaleApiIndex", queryError.GetProperty("code").GetString());
        var message = queryError.GetProperty("message").GetString()!;
        Assert.Contains("environment snapshot changed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("s1atlas index", message, StringComparison.Ordinal);
        Assert.DoesNotContain("not selected build", message, StringComparison.Ordinal);
        Assert.DoesNotContain(buildId, message, StringComparison.Ordinal);

        var doctor = Run("doctor", "--json");
        using var doctorJson = JsonDocument.Parse(doctor.Output);
        var apiItem = doctorJson.RootElement.GetProperty("data").GetProperty("items")
            .EnumerateArray().Single(item => item.GetProperty("id").GetString() == ReadinessItemIds.ApiIndex);
        Assert.Equal("stale", apiItem.GetProperty("state").GetString());
        var detail = apiItem.GetProperty("detail").GetString()!;
        const string fix = "s1atlas index --codebase s1api --channel installed";
        Assert.Contains("environment snapshot changed", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fix, detail, StringComparison.Ordinal);
        Assert.Equal(fix, apiItem.GetProperty("fixCommand").GetString());
        Assert.DoesNotContain(buildId, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No installed S1API index exists", detail, StringComparison.Ordinal);

        var humanDoctor = Run("doctor");
        Assert.Contains("environment snapshot changed", humanDoctor.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fix, humanDoctor.Output, StringComparison.Ordinal);

        var reindex = Run("index", "--codebase", "s1api", "--channel", "installed");
        Assert.Equal(0, reindex.ExitCode);
        Assert.Equal("ok", GetDoctorItemState(Run("doctor", "--json").Output, ReadinessItemIds.ApiIndex));
        Assert.Equal(0, Run("attributes", "System.ObsoleteAttribute", "--codebase", "s1api", "--json").ExitCode);
    }

    [Fact]
    public async Task Rescanned_dll_hash_marks_installed_index_stale_until_reindex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildId = new string('a', 64);
        var assemblyPath = Path.Combine(_dataDirectory, "game", "Mods", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var firstHash = await Sha256OfFileAsync(assemblyPath, cancellationToken);
        await SaveSnapshotAsync(
            buildId,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true, firstHash));
        await SeedRunnableGameExtractionAsync(buildId, "stale");
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var firstIndexOutput = new StringWriter();
        using var firstIndexError = new StringWriter();

        var firstIndexExitCode = application.Invoke(["index"], firstIndexOutput, firstIndexError, cancellationToken);

        Assert.Equal(0, firstIndexExitCode);

        File.Copy(
            typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location,
            assemblyPath,
            overwrite: true);
        var secondHash = await Sha256OfFileAsync(assemblyPath, cancellationToken);
        Assert.NotEqual(firstHash, secondHash);
        await SaveSnapshotAsync(
            buildId,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true, secondHash));

        using var staleDoctorOutput = new StringWriter();
        using var staleDoctorError = new StringWriter();
        _ = application.Invoke(["doctor", "--json"], staleDoctorOutput, staleDoctorError, cancellationToken);
        Assert.Equal("stale", GetDoctorItemState(staleDoctorOutput.ToString(), ReadinessItemIds.ApiIndex));

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository, _dataDirectory));
        var staleCatalog = await service.ListAsync(null, cancellationToken);
        Assert.Equal(
            ApiIndexAvailability.Stale,
            S1ApiInstalledSelection(staleCatalog).Availability);

        using var reindexOutput = new StringWriter();
        using var reindexError = new StringWriter();
        var reindexExitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed"],
            reindexOutput,
            reindexError,
            cancellationToken);
        Assert.Equal(0, reindexExitCode);

        using var okDoctorOutput = new StringWriter();
        using var okDoctorError = new StringWriter();
        _ = application.Invoke(["doctor", "--json"], okDoctorOutput, okDoctorError, cancellationToken);
        Assert.Equal("ok", GetDoctorItemState(okDoctorOutput.ToString(), ReadinessItemIds.ApiIndex));

        var okCatalog = await service.ListAsync(null, cancellationToken);
        var okSelection = S1ApiInstalledSelection(okCatalog);
        Assert.Equal(ApiIndexAvailability.Current, okSelection.Availability);
        Assert.Equal(secondHash, okSelection.SourceIdentity);
    }

    [Fact]
    public async Task Installed_s1api_indexes_are_pinned_per_build()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildA = new string('a', 64);
        var buildB = new string('b', 64);
        var assemblyA = Path.Combine(_dataDirectory, "game-a", "Mods", "S1API.dll");
        var assemblyB = Path.Combine(_dataDirectory, "game-b", "Mods", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyB)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyA);
        File.Copy(typeof(S1Atlas.InteropAssemblyFixture.InteropFixtureRoot).Assembly.Location, assemblyB);
        var hashA = await Sha256OfFileAsync(assemblyA, cancellationToken);
        var hashB = await Sha256OfFileAsync(assemblyB, cancellationToken);
        Assert.NotEqual(hashA, hashB);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");

        await SaveSnapshotAsync(
            buildA,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyA, true, hashA));
        await SeedRunnableGameExtractionAsync(buildA, "build-a");
        using var indexAOutput = new StringWriter();
        using var indexAError = new StringWriter();
        var indexAExitCode = application.Invoke(["index"], indexAOutput, indexAError, cancellationToken);
        Assert.Equal(0, indexAExitCode);

        await SaveSnapshotAsync(
            buildB,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyB, true, hashB));
        await SeedRunnableGameExtractionAsync(buildB, "build-b");
        using var indexBOutput = new StringWriter();
        using var indexBError = new StringWriter();
        var indexBExitCode = application.Invoke(["index"], indexBOutput, indexBError, cancellationToken);
        Assert.Equal(0, indexBExitCode);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var indexA = await repository.GetLatestCompletedIndexForBuildAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            buildA,
            cancellationToken);
        var indexB = await repository.GetLatestCompletedIndexForBuildAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            buildB,
            cancellationToken);
        Assert.NotNull(indexA);
        Assert.NotNull(indexB);
        Assert.NotEqual(indexA!.IndexId, indexB!.IndexId);

        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository, _dataDirectory));
        var catalog = await service.ListAsync(null, cancellationToken);
        Assert.Equal(buildB, catalog.ResolvedBuildId);
        var selection = S1ApiInstalledSelection(catalog);
        Assert.Equal(ApiIndexAvailability.Current, selection.Availability);
        Assert.Equal(hashB, selection.SourceIdentity);
    }

    [Fact]
    public async Task Default_index_completes_when_an_installed_s1api_index_already_exists_for_the_same_dll()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buildId = new string('a', 64);
        var assemblyPath = Path.Combine(_dataDirectory, "game", "Mods", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var dllHash = await Sha256OfFileAsync(assemblyPath, cancellationToken);
        await SaveSnapshotAsync(
            buildId,
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true, dllHash));
        await SeedRunnableGameExtractionAsync(buildId, "rebuild");
        var application = new CliApplication(_dataDirectory, "0.1.0-test");

        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        var firstExitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--force"],
            firstOutput,
            firstError,
            cancellationToken);
        Assert.Equal(0, firstExitCode);

        using var indexOutput = new StringWriter();
        using var indexError = new StringWriter();
        var indexExitCode = application.Invoke(["index"], indexOutput, indexError, cancellationToken);

        Assert.Equal(0, indexExitCode);
        Assert.Contains("S1Api Installed", indexOutput.ToString(), StringComparison.Ordinal);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var service = new ApiIndexQueryService(repository, new IndexQueryService(repository, _dataDirectory));
        var catalog = await service.ListAsync(null, cancellationToken);
        var selection = S1ApiInstalledSelection(catalog);
        Assert.Equal(ApiIndexAvailability.Current, selection.Availability);
        Assert.Equal(dllHash, selection.SourceIdentity);
    }

    private static ApiIndexSelection S1ApiInstalledSelection(ApiIndexCatalogResult catalog) =>
        catalog.Selections.Single(selection =>
            selection.Codebase == CodebaseKind.S1Api && selection.Channel == CodeChannel.Installed);

    private static string GetDoctorItemState(string doctorJson, string itemId)
    {
        using var json = JsonDocument.Parse(doctorJson);
        var items = json.RootElement.GetProperty("data").GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == itemId)
            {
                return item.GetProperty("state").GetString()!;
            }
        }

        throw new InvalidOperationException($"Doctor output has no item '{itemId}'.");
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task SeedRunnableGameExtractionAsync(string buildId, string seedSuffix)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        var toolInstanceId = "tool-acceptance-" + seedSuffix;
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
        var recipeId = ExtractionSeed.Sha256("recipe-acceptance-" + seedSuffix);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var baseTime = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var profileDigest = ExtractionSeed.Sha256("profile-acceptance-" + seedSuffix);
        var policyDigest = ExtractionSeed.Sha256("policy-acceptance-" + seedSuffix);
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

    private async Task<EnvironmentSnapshot> SaveSnapshotAsync(string buildId, DependencyVersion apiDependency)
    {
        var snapshot = new EnvironmentSnapshot(
            2,
            new GameBuild(
                buildId,
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
