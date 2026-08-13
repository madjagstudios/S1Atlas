using System.Text.Json;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

public sealed class ExtractionCliTests
{
    private const string DefaultProfileId =
        "cpp2il-reconstructed-assemblies-v1";

    [Fact]
    public async Task RootHelp_ListsExtractAndEverySupportedOption()
    {
        await using var fixture = new ExtractionCliFixture();

        var rootHelp = fixture.Invoke("--help");
        var extractHelp = fixture.Invoke("extract", "--help");

        Assert.Equal(0, rootHelp.ExitCode);
        Assert.Contains("extract", rootHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, extractHelp.ExitCode);
        foreach (var option in new[]
                 {
                     "--build", "--game-path", "--cpp2il-path", "--profile",
                     "--retry", "--snapshot-inputs", "--keep-failed-artifacts", "--json"
                 })
        {
            Assert.Contains(option, extractHelp.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AtlasAndConfigurationPaths_ExposeOnlyValidatedBuildSegments()
    {
        await using var fixture = new ExtractionCliFixture();
        var paths = new AtlasPaths(fixture.DataDirectory);
        var buildId = new string('a', 64);

        Assert.Equal(
            Path.Combine(fixture.DataDirectory, "builds"),
            paths.BuildsDirectory);
        Assert.Equal(
            Path.Combine(fixture.DataDirectory, "extraction.lock"),
            paths.ExtractionLockPath);
        Assert.Equal(
            Path.Combine(fixture.DataDirectory, "builds", buildId),
            paths.GetBuildDirectory(buildId));
        Assert.Equal(
            Path.Combine(fixture.DataDirectory, "builds", buildId, "attempts"),
            paths.GetBuildAttemptsDirectory(buildId));
        Assert.Equal(
            Path.Combine(
                fixture.DataDirectory,
                "builds",
                buildId,
                "extractions",
                ".staging"),
            paths.GetBuildExtractionStagingDirectory(buildId));
        Assert.Equal(
            Path.Combine(fixture.DataDirectory, "builds", buildId, "inputs"),
            paths.GetBuildInputsDirectory(buildId));
        Assert.Equal(
            Path.Combine(
                fixture.DataDirectory,
                "builds",
                buildId,
                "inputs",
                ".staging"),
            paths.GetBuildInputStagingDirectory(buildId));

        foreach (var invalid in new[]
                 {
                     new string('a', 63),
                     new string('A', 64),
                     "../" + new string('a', 61),
                     new string('g', 64)
                 })
        {
            Assert.Throws<ArgumentException>(() => paths.GetBuildDirectory(invalid));
        }

        var configuration = CliConfigurationPaths.Resolve();
        Assert.True(Directory.Exists(configuration.ToolDefinitionsDirectory));
        Assert.True(Directory.Exists(configuration.ExtractionProfilesDirectory));
        Assert.True(Directory.Exists(configuration.ValidationPoliciesDirectory));
    }

    // Phase 4 legitimately changes this assertion: `extract` now validates and promotes the
    // candidate, so a valid managed candidate produces authoritative output (not a bare
    // ProcessCompleted candidate). A custom tool validates but is never auto-preferred.
    [Fact]
    public async Task Extract_DefaultCurrentBuildAndCustomTool_ValidatesAndPromotesAuthoritatively()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var buildId = await fixture.SeedBuildAsync();
        var before = await fixture.GetAuthoritativeHashesAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        var after = await fixture.GetAuthoritativeHashesAsync();
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertSuccessEnvelope(root, "extract");
        var data = root.GetProperty("data");
        Assert.Equal(buildId, data.GetProperty("buildId").GetString());
        Assert.Equal("CustomOverride", data.GetProperty("toolTrustLevel").GetString());
        Assert.Equal("Valid", data.GetProperty("validationOutcome").GetString());
        Assert.True(data.GetProperty("processWasRun").GetBoolean());
        Assert.True(data.GetProperty("validationWasRun").GetBoolean());
        Assert.False(data.GetProperty("reusedExistingExtraction").GetBoolean());
        Assert.True(data.GetProperty("authoritative").GetBoolean());
        Assert.False(data.GetProperty("preferred").GetBoolean());
        Assert.Equal(before, after);
        var extractionRoot = data.GetProperty("extractionRoot").GetString()!;
        Assert.True(File.Exists(Path.Combine(extractionRoot, "complete.marker")));
    }

    // Phase 4 legitimately changes this assertion: successful `extract` now reports the
    // authoritative validated extraction instead of the Phase 3 candidate caveat.
    [Fact]
    public async Task Extract_HumanSuccess_StatesTheAuthoritativeExtraction()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains(
            "Authoritative validated extraction is available.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("Extraction:    ", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Root:          ", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Tool trust:    CustomOverride", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Validation:    Valid", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Preferred:     no", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_ExplicitKnownBuild_Completes()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var buildId = await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--build",
            buildId,
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--profile",
            DefaultProfileId,
            "--retry",
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(buildId, data.GetProperty("buildId").GetString());
        Assert.True(data.GetProperty("authoritative").GetBoolean());
        Assert.True(data.GetProperty("processWasRun").GetBoolean());
    }

    [Fact]
    public async Task Extract_UnknownBuild_ReturnsStageAwareJsonFailure()
    {
        await using var fixture = new ExtractionCliFixture();
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--build",
            new string('f', 64),
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        AssertFailureEnvelope(root, "extract", expectedExitCode: 1);
        var error = root.GetProperty("error");
        Assert.Equal("InputResolution", error.GetProperty("stage").GetString());
        Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("attemptId", out _));
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Extract_WithoutCurrentBuild_ReturnsStructuredScanGuidance()
    {
        await using var fixture = new ExtractionCliFixture();

        var result = fixture.Invoke("extract", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
        Assert.Contains(
            "s1atlas scan",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_WithoutManagedTool_ProvidesOfflineInstallGuidance()
    {
        await using var fixture = new ExtractionCliFixture();
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke("extract", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("ToolResolution", error.GetProperty("stage").GetString());
        Assert.Equal("ToolNotInstalled", error.GetProperty("code").GetString());
        Assert.Contains(
            "s1atlas tools install cpp2il",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_CustomToolProbeFailure_IsStructuredAndOffline()
    {
        await using var fixture = new ExtractionCliFixture();
        await fixture.SeedBuildAsync();
        fixture.RequireProbeFailure();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("ToolResolution", error.GetProperty("stage").GetString());
        Assert.Equal("ToolProbeFailed", error.GetProperty("code").GetString());
    }

    // Phase 4 legitimately changes this assertion: the authoritative `extract` output no
    // longer exposes candidateOutputPath and the attempt document's resolved paths are
    // cleared once validation rewrites it, so the resolved game root is observed directly
    // at the process seam.
    [Fact]
    public async Task Extract_ExplicitGamePath_TakesPriorityOverStoredObservation()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();
        await fixture.CreateGameAsync(fixture.AlternateGameDirectory);

        var result = fixture.Invoke(
            "extract",
            "--game-path",
            fixture.AlternateGameDirectory,
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            Path.GetFullPath(fixture.AlternateGameDirectory),
            Path.GetFullPath(extractor.ObservedGameRoot!));
    }

    [Fact]
    public async Task Extract_PreRunMismatch_RejectsBeforeProcessAndRecommendsScan()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.NonZero);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();
        await File.AppendAllTextAsync(
            Path.Combine(fixture.GameDirectory, "GameAssembly.dll"),
            "changed before extraction",
            TestContext.Current.CancellationToken);

        var result = fixture.Invoke(
            "extract",
            "--game-path",
            fixture.GameDirectory,
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("BuildInputMismatch", error.GetProperty("code").GetString());
        Assert.Equal("PreRunInputVerification", error.GetProperty("stage").GetString());
        Assert.Contains(
            "s1atlas scan",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.False(extractor.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task Extract_PostRunMutation_RejectsCandidateOutput()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.MutateInput);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(
            "InputChangedDuringExtraction",
            error.GetProperty("code").GetString());
        Assert.Equal(
            "PostRunInputVerification",
            error.GetProperty("stage").GetString());
        var attemptRoot = GetAttemptRoot(fixture, error);
        Assert.False(Directory.Exists(Path.Combine(attemptRoot, "candidate-output")));
    }

    [Fact]
    public async Task Extract_NonzeroChildExit_ReportsAttemptAndLogPaths()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.NonZero);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("ProcessExitNonZero", error.GetProperty("code").GetString());
        Assert.Equal("ProcessExecution", error.GetProperty("stage").GetString());
        var attemptRoot = GetAttemptRoot(fixture, error);
        Assert.True(File.Exists(Path.Combine(attemptRoot, "logs", "stdout.log")));
        Assert.True(File.Exists(Path.Combine(attemptRoot, "logs", "stderr.log")));
    }

    [Fact]
    public async Task Extract_Timeout_MapsToOperationalExitOne()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.TimedOut);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "ProcessTimedOut",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task Extract_CallerCancellation_UsesInvokeTokenAndExitsTwo()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.Canceled);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();
        using var cancellation = new CancellationTokenSource();

        var invocation = Task.Run(
            () => fixture.InvokeWithCancellation(
                cancellation.Token,
                "extract",
                "--cpp2il-path",
                fixture.FakeCpp2IlPath,
                "--json"),
            TestContext.Current.CancellationToken);
        await extractor.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await invocation;

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(cancellation.Token, extractor.ObservedCancellationToken);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(
            "OperationCanceled",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    // Phase 4 legitimately changes this assertion: the authoritative `extract` output no
    // longer surfaces inputSnapshotId, so the snapshot is located through the attempt row.
    [Fact]
    public async Task Extract_SnapshotInputs_RetainsUnverifiedReplaySnapshot()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--snapshot-inputs",
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        var data = document.RootElement.GetProperty("data");
        var buildId = data.GetProperty("buildId").GetString()!;
        var attemptId = data.GetProperty("attemptId").GetString()!;

        var repository = new SqliteAtlasRepository(fixture.DatabasePath);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var attempt = await repository.GetAttemptAsync(
            attemptId, TestContext.Current.CancellationToken);
        var snapshotId = attempt!.InputSnapshotId;
        Assert.False(string.IsNullOrWhiteSpace(snapshotId));
        var snapshotRoot = Path.Combine(
            fixture.DataDirectory, "builds", buildId, "inputs", snapshotId!);
        Assert.True(File.Exists(Path.Combine(snapshotRoot, "input-manifest.json")));
        Assert.True(File.Exists(Path.Combine(snapshotRoot, "complete.marker")));
        Assert.Empty(await repository.ListReplayVerifiedInputSnapshotsAsync(
            buildId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_KeepFailedArtifacts_RetainsOnlyPartialOutput()
    {
        var extractor = new ScriptedProcessExtractor(ScriptedProcessOutcome.NonZero);
        await using var fixture = new ExtractionCliFixture(extractor);
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--keep-failed-artifacts",
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var error = document.RootElement.GetProperty("error");
        var attemptRoot = GetAttemptRoot(fixture, error);
        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(
            attemptRoot,
            "retained-output",
            "partial-or-candidate.dll")));
        Assert.False(Directory.Exists(Path.Combine(attemptRoot, "candidate-output")));
    }

    [Fact]
    public async Task Extract_JsonStdout_IsExactlyOneDocumentWithoutProgress()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("extract", document.RootElement.GetProperty("command").GetString());
        Assert.DoesNotContain(
            "Authoritative validated extraction",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Extract_HumanFailure_ContainsNoStackTrace()
    {
        await using var fixture = new ExtractionCliFixture();

        var result = fixture.Invoke("extract");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("s1atlas scan", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Stage:   InputResolution", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Code:    BuildNotFound", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
    }

    // Phase 4 legitimately changes this assertion: the source attempt is now driven to
    // Succeeded with a result extraction ID, and the authoritative extraction root is the
    // contained, immutable output carrying complete.marker.
    [Fact]
    public async Task Extract_ExtractionAndAttemptFacts_RemainContainedAndAgree()
    {
        await using var fixture = new ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract",
            "--cpp2il-path",
            fixture.FakeCpp2IlPath,
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        var extractionId = data.GetProperty("extractionId").GetString()!;
        var extractionRoot = Path.GetFullPath(
            data.GetProperty("extractionRoot").GetString()!);
        var atlasPrefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(fixture.DataDirectory)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(atlasPrefix, extractionRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(extractionRoot));
        Assert.True(File.Exists(Path.Combine(extractionRoot, "complete.marker")));

        var attemptId = data.GetProperty("attemptId").GetString()!;
        var repository = new SqliteAtlasRepository(fixture.DatabasePath);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var stored = await repository.GetAttemptAsync(
            attemptId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, stored.Status);
        Assert.Equal(extractionId, stored.ResultExtractionId);
    }

    [Fact]
    public async Task ExistingJsonContracts_OmitNewExtractionOnlyErrorFacts()
    {
        await using var fixture = new ExtractionCliFixture();
        await fixture.SeedBuildAsync();

        foreach (var arguments in new[]
                 {
                     new[] { "status", "--json" },
                     new[] { "env", "--json" },
                     new[] { "builds", "--json" },
                     new[] { "tools", "status", "cpp2il", "--json" }
                 })
        {
            var result = fixture.Invoke(arguments);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                new[] { "schemaVersion", "command", "success", "exitCode", "data", "error" },
                document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        }

        var failure = fixture.Invoke("tools", "status", "unknown", "--json");
        using var failureDocument = JsonDocument.Parse(failure.StandardOutput);
        Assert.Equal(
            new[] { "code", "message" },
            failureDocument.RootElement
                .GetProperty("error")
                .EnumerateObject()
                .Select(property => property.Name));
    }

    private static string GetAttemptRoot(
        ExtractionCliFixture fixture,
        JsonElement error)
    {
        var attemptId = error.GetProperty("attemptId").GetString()!;
        var attempts = Directory.EnumerateFiles(
                Path.Combine(fixture.DataDirectory, "builds"),
                "attempt.json",
                SearchOption.AllDirectories)
            .Where(path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                attemptId,
                StringComparison.Ordinal))
            .ToArray();
        return Path.GetDirectoryName(Assert.Single(attempts))!;
    }

    private static void AssertSuccessEnvelope(JsonElement root, string command)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    private static void AssertFailureEnvelope(
        JsonElement root,
        string command,
        int expectedExitCode)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(expectedExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }
}
