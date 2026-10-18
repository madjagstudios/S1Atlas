using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Application.Readiness;
using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

public sealed class Phase4ExtractionCliTests
{
    [Fact]
    public async Task Extract_ManagedValidCandidate_ValidatesPromotesAndAutoPrefersWithoutRerun()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke("extract", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertSuccessEnvelope(root, "extract");
        var data = root.GetProperty("data");
        Assert.Equal("ManagedPinned", data.GetProperty("toolTrustLevel").GetString());
        Assert.Equal("Valid", data.GetProperty("validationOutcome").GetString());
        Assert.True(data.GetProperty("authoritative").GetBoolean());
        Assert.True(data.GetProperty("preferred").GetBoolean());
        Assert.True(data.GetProperty("processWasRun").GetBoolean());
        var extractionRoot = data.GetProperty("extractionRoot").GetString()!;
        Assert.True(File.Exists(Path.Combine(extractionRoot, "complete.marker")));
    }

    [Fact]
    public async Task Extract_SecondRun_ReusesExistingExtractionWithFullIntegrityNoOp()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
        await fixture.SeedBuildAsync();

        var first = fixture.Invoke("extract", "--json");
        var second = fixture.Invoke("extract", "--json");

        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        var firstData = firstDocument.RootElement.GetProperty("data");
        var secondData = secondDocument.RootElement.GetProperty("data");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(
            firstData.GetProperty("extractionId").GetString(),
            secondData.GetProperty("extractionId").GetString());
        Assert.True(secondData.GetProperty("reusedExistingExtraction").GetBoolean());
        Assert.False(secondData.GetProperty("processWasRun").GetBoolean());
        Assert.False(secondData.GetProperty("validationWasRun").GetBoolean());
        Assert.True(secondData.GetProperty("authoritative").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("attemptId").ValueKind);
    }

    [Fact]
    public async Task Extract_CorruptPreference_ReturnsCodedFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
        var buildId = await fixture.SeedBuildAsync();
        var first = fixture.Invoke("extract", "--json");
        Assert.Equal(0, first.ExitCode);
        using var firstJson = JsonDocument.Parse(first.StandardOutput);
        var extractionId = firstJson.RootElement.GetProperty("data")
            .GetProperty("extractionId").GetString()!;
        var repository = await fixture.OpenRepositoryAsync();
        var attemptCount = (await repository.ListAttemptsAsync(buildId, ct)).Count;
        var artifact = Path.Combine(
            fixture.ValidatedExtractionRoot(buildId, extractionId),
            "reconstructed", "Assembly-CSharp.dll");
        await File.AppendAllTextAsync(artifact, "corruption", ct);

        var result = fixture.Invoke("extract", "--json");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        AssertFailureEnvelope(json.RootElement, "extract", 1);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("IntegrityMismatch", error.GetProperty("code").GetString());
        Assert.Equal("Recovery", error.GetProperty("stage").GetString());
        var jsonPreferred = await repository.GetPreferredExtractionAsync(buildId, ct);
        var jsonAttemptCount = (await repository.ListAttemptsAsync(buildId, ct)).Count;

        var human = fixture.Invoke("extract");
        var humanPreferred = await repository.GetPreferredExtractionAsync(buildId, ct);
        var humanAttemptCount = (await repository.ListAttemptsAsync(buildId, ct)).Count;
        var retryCommand = $"s1atlas extract --retry --build {buildId}";
        Assert.Multiple(
            () => Assert.Contains(retryCommand, error.GetProperty("message").GetString()!),
            () => Assert.Equal(extractionId, jsonPreferred?.ExtractionId),
            () => Assert.Equal(attemptCount, jsonAttemptCount),
            () => Assert.Equal(1, human.ExitCode),
            () => Assert.Equal(string.Empty, human.StandardOutput),
            () => Assert.Contains("integrity", human.StandardError, StringComparison.OrdinalIgnoreCase),
            () => Assert.Contains(retryCommand, human.StandardError),
            () => Assert.DoesNotContain("Exception", human.StandardError),
            () => Assert.DoesNotMatch(@"(?m)^\s+at\s", human.StandardError),
            () => Assert.Equal(extractionId, humanPreferred?.ExtractionId),
            () => Assert.Equal(attemptCount, humanAttemptCount));
    }

    [Fact]
    public async Task Doctor_CorruptExtraction_ProgressesFromRetryToIndexAfterRepair()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallReadinessTools();
        var buildId = await fixture.SeedBuildAsync();
        var first = fixture.Invoke("extract", "--json");
        Assert.Equal(0, first.ExitCode);
        using var firstJson = JsonDocument.Parse(first.StandardOutput);
        var extractionId = firstJson.RootElement.GetProperty("data")
            .GetProperty("extractionId").GetString()!;
        // No index is created, so a repaired extraction must advance to index.
        await File.AppendAllTextAsync(Path.Combine(
            fixture.ValidatedExtractionRoot(buildId, extractionId),
            "reconstructed", "Assembly-CSharp.dll"), "corruption", TestContext.Current.CancellationToken);

        AssertDoctor("missing", ReadinessFixCommands.ExtractRetry);

        var plain = fixture.Invoke("extract", "--json");
        Assert.Equal(1, plain.ExitCode);
        using var plainJson = JsonDocument.Parse(plain.StandardOutput);
        Assert.Equal("IntegrityMismatch", plainJson.RootElement.GetProperty("error")
            .GetProperty("code").GetString());
        AssertDoctor("missing", ReadinessFixCommands.ExtractRetry);

        var retry = fixture.Invoke("extract", "--retry", "--json");
        Assert.True(retry.ExitCode == 0, retry.StandardOutput + retry.StandardError);
        AssertDoctor("ok", ReadinessFixCommands.Index);

        void AssertDoctor(string extractionState, string nextCommand)
        {
            var doctor = fixture.Invoke("doctor", "--json");
            using var document = JsonDocument.Parse(doctor.StandardOutput);
            var data = document.RootElement.GetProperty("data");
            var extraction = Assert.Single(data.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetString() == ReadinessItemIds.Extraction);
            Assert.Equal(extractionState, extraction.GetProperty("state").GetString());
            if (extractionState == "missing")
            {
                Assert.Equal(ReadinessFixCommands.ExtractRetry, extraction.GetProperty("fixCommand").GetString());
            }
            Assert.Equal(nextCommand, data.GetProperty("nextStep").GetProperty("command").GetString());
        }
    }

    [Fact]
    public async Task Extract_InvalidCandidate_ExitsOneWithNoMarkerOrPreference()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.InvalidManagedAssembly));
        fixture.InstallManagedTool();
        var buildId = await fixture.SeedBuildAsync();

        var result = fixture.Invoke("extract", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        AssertFailureEnvelope(document.RootElement, "extract", 1);
        Assert.Equal(
            "ExtractionNotAuthoritative",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(string.Empty, result.StandardError);

        var repository = await fixture.OpenRepositoryAsync();
        Assert.Empty(await repository.ListValidatedExtractionsAsync(
            buildId, TestContext.Current.CancellationToken));
        Assert.Null(await repository.GetPreferredExtractionAsync(
            buildId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_CustomTool_ValidatesButIsNotAutoPreferred()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var buildId = await fixture.SeedBuildAsync();

        var result = fixture.Invoke(
            "extract", "--cpp2il-path", fixture.CustomToolPath, "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("CustomOverride", data.GetProperty("toolTrustLevel").GetString());
        Assert.True(data.GetProperty("authoritative").GetBoolean());
        Assert.False(data.GetProperty("preferred").GetBoolean());

        var repository = await fixture.OpenRepositoryAsync();
        Assert.Null(await repository.GetPreferredExtractionAsync(
            buildId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extractions_ListShowPromote_ProduceExactlyOneJsonDocumentEach()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var buildId = await fixture.SeedBuildAsync();
        var extractionId = ExtractCustom(fixture);

        var list = fixture.Invoke("extractions", "list", "--json");
        var show = fixture.Invoke("extractions", "show", extractionId, "--json");
        var promote = fixture.Invoke("extractions", "promote", extractionId, "--json");

        foreach (var (result, command) in new[]
                 {
                     (list, "extractions list"),
                     (show, "extractions show"),
                     (promote, "extractions promote")
                 })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            AssertSuccessEnvelope(document.RootElement, command);
        }

        using var listDocument = JsonDocument.Parse(list.StandardOutput);
        var entries = listDocument.RootElement.GetProperty("data").GetProperty("entries");
        Assert.Contains(
            entries.EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == extractionId);

        using var promoteDocument = JsonDocument.Parse(promote.StandardOutput);
        Assert.True(promoteDocument.RootElement
            .GetProperty("data").GetProperty("preferred").GetBoolean());

        var repository = await fixture.OpenRepositoryAsync();
        var preferred = await repository.GetPreferredExtractionAsync(
            buildId, TestContext.Current.CancellationToken);
        Assert.Equal(extractionId, preferred!.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, preferred.SelectionReason);
    }

    [Fact]
    public async Task ExtractionsShow_Extraction_PerformsFullIntegrity()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();
        var extractionId = ExtractCustom(fixture);

        var result = fixture.Invoke("extractions", "show", extractionId, "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Extraction", data.GetProperty("kind").GetString());
        var extraction = data.GetProperty("extraction");
        Assert.Equal(extractionId, extraction.GetProperty("extractionId").GetString());
        Assert.True(extraction.GetProperty("integrityVerified").GetBoolean());
    }

    [Fact]
    public async Task ExtractionsShow_Attempt_ReturnsAttemptFacts()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var extract = fixture.Invoke(
            "extract", "--cpp2il-path", fixture.CustomToolPath, "--json");
        using var extractDocument = JsonDocument.Parse(extract.StandardOutput);
        var attemptId = extractDocument.RootElement
            .GetProperty("data").GetProperty("attemptId").GetString()!;

        var result = fixture.Invoke("extractions", "show", attemptId, "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Attempt", data.GetProperty("kind").GetString());
        Assert.Equal("Succeeded", data.GetProperty("attempt").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExtractionsPromote_AttemptId_IsRejected()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var extract = fixture.Invoke(
            "extract", "--cpp2il-path", fixture.CustomToolPath, "--json");
        using var extractDocument = JsonDocument.Parse(extract.StandardOutput);
        var attemptId = extractDocument.RootElement
            .GetProperty("data").GetProperty("attemptId").GetString()!;

        var result = fixture.Invoke("extractions", "promote", attemptId, "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        AssertFailureEnvelope(document.RootElement, "extractions promote", 1);
        Assert.Equal(
            "AttemptNotPromotable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExtractionsList_IncludeFailed_AddsFailedAttempts()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.InvalidManagedAssembly));
        await fixture.SeedBuildAsync();

        var failed = fixture.Invoke(
            "extract", "--cpp2il-path", fixture.CustomToolPath, "--json");
        Assert.Equal(1, failed.ExitCode);

        var withoutFailed = fixture.Invoke("extractions", "list", "--json");
        var withFailed = fixture.Invoke("extractions", "list", "--include-failed", "--json");

        using var withoutDocument = JsonDocument.Parse(withoutFailed.StandardOutput);
        using var withDocument = JsonDocument.Parse(withFailed.StandardOutput);
        Assert.Empty(withoutDocument.RootElement
            .GetProperty("data").GetProperty("entries").EnumerateArray());
        Assert.Contains(
            withDocument.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("kind").GetString() == "Attempt" &&
                entry.GetProperty("status").GetString() == "Failed");
    }

    [Fact]
    public async Task ChangedFinalArtifact_FailsShowAndPromoteAndClearsPreferredPointer()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var buildId = await fixture.SeedBuildAsync();
        var extractionId = ExtractCustom(fixture);

        var promote = fixture.Invoke("extractions", "promote", extractionId, "--json");
        Assert.Equal(0, promote.ExitCode);

        var artifact = Path.Combine(
            fixture.ValidatedExtractionRoot(buildId, extractionId),
            "reconstructed",
            "Assembly-CSharp.dll");
        await File.AppendAllTextAsync(
            artifact, "corruption", TestContext.Current.CancellationToken);

        var show = fixture.Invoke("extractions", "show", extractionId, "--json");
        var repromote = fixture.Invoke("extractions", "promote", extractionId, "--json");

        using var showDocument = JsonDocument.Parse(show.StandardOutput);
        using var repromoteDocument = JsonDocument.Parse(repromote.StandardOutput);
        Assert.Equal(1, show.ExitCode);
        Assert.Equal(
            "IntegrityMismatch",
            showDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, repromote.ExitCode);

        var repository = await fixture.OpenRepositoryAsync();
        Assert.Null(await repository.GetPreferredExtractionAsync(
            buildId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_RetryAfterCorruption_RepairsSameExtraction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
        var buildId = await fixture.SeedBuildAsync();

        var first = fixture.Invoke("extract", "--json");
        Assert.Equal(0, first.ExitCode);
        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        var firstData = firstDocument.RootElement.GetProperty("data");
        var originalExtractionId = firstData.GetProperty("extractionId").GetString()!;
        var originalAttemptId = firstData.GetProperty("attemptId").GetString()!;
        // Repair must preserve even a manual selection, including its timestamp and history.
        var repository = await fixture.OpenRepositoryAsync();
        await repository.SetPreferredExtractionAsync(new PreferredExtraction(
            buildId, originalExtractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion), ct);
        var originalExtraction = await repository.GetValidatedExtractionAsync(originalExtractionId, ct);
        var originalArtifacts = await repository.GetExtractionArtifactsAsync(originalExtractionId, ct);
        var originalAttempts = await repository.ListAttemptsAsync(buildId, ct);
        var originalPreference = await repository.GetPreferredExtractionAsync(buildId, ct);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, originalPreference!.SelectionReason);
        await using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync(ct);
        async Task<string> ReadHistoryAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<object[]>();
            while (await reader.ReadAsync(ct))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            return JsonSerializer.Serialize(rows);
        }
        var preferenceHistory = await ReadHistoryAsync("SELECT * FROM extraction_preference_events ORDER BY event_id");
        var originalValidationSql = $"SELECT * FROM extraction_validation_results WHERE attempt_id = '{originalAttemptId}'";
        var originalValidation = await ReadHistoryAsync(originalValidationSql);

        var artifact = Path.Combine(
            fixture.ValidatedExtractionRoot(buildId, originalExtractionId),
            "reconstructed",
            "Assembly-CSharp.dll");
        var originalBytes = await File.ReadAllBytesAsync(artifact, ct);
        await File.AppendAllTextAsync(artifact, "corruption", ct);
        var damagedBytes = await File.ReadAllBytesAsync(artifact, ct);

        var retry = fixture.Invoke("extract", "--retry", "--json");

        Assert.True(retry.ExitCode == 0, retry.StandardOutput + retry.StandardError);
        using var retryJson = JsonDocument.Parse(retry.StandardOutput);
        var data = retryJson.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("processWasRun").GetBoolean());
        Assert.True(data.GetProperty("validationWasRun").GetBoolean());
        Assert.True(data.GetProperty("authoritative").GetBoolean());
        Assert.True(data.GetProperty("preferred").GetBoolean());
        Assert.False(data.GetProperty("reusedExistingExtraction").GetBoolean());
        Assert.Equal(originalExtractionId, data.GetProperty("extractionId").GetString());
        var retryAttemptId = data.GetProperty("attemptId").GetString()!;
        Assert.NotEqual(originalAttemptId, retryAttemptId);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(artifact, ct));
        var quarantine = Path.Combine(fixture.DataDirectory, "builds", buildId, "extractions",
            "quarantine", $"{originalExtractionId}-{retryAttemptId}");
        Assert.Equal(damagedBytes, await File.ReadAllBytesAsync(
            Path.Combine(quarantine, "reconstructed", "Assembly-CSharp.dll"), ct));

        var show = fixture.Invoke("extractions", "show", originalExtractionId, "--json");
        Assert.Equal(0, show.ExitCode);
        using var showJson = JsonDocument.Parse(show.StandardOutput);
        Assert.True(showJson.RootElement.GetProperty("data").GetProperty("extraction")
            .GetProperty("integrityVerified").GetBoolean());
        Assert.Equal(originalPreference, await repository.GetPreferredExtractionAsync(buildId, ct));
        Assert.Equal(preferenceHistory, await ReadHistoryAsync("SELECT * FROM extraction_preference_events ORDER BY event_id"));
        Assert.Equal(originalValidation, await ReadHistoryAsync(originalValidationSql));
        var extractions = await repository.ListValidatedExtractionsAsync(buildId, ct);
        var repaired = Assert.Single(extractions);
        Assert.Equal(originalAttemptId, repaired.SourceAttemptId);
        Assert.Equal(JsonSerializer.Serialize(originalExtraction), JsonSerializer.Serialize(repaired));
        Assert.Equal(originalArtifacts, await repository.GetExtractionArtifactsAsync(originalExtractionId, ct));
        var attempts = await repository.ListAttemptsAsync(buildId, ct);
        Assert.Equal(originalAttempts.Count + 1, attempts.Count);
        foreach (var original in originalAttempts)
        {
            Assert.Equal(JsonSerializer.Serialize(original),
                JsonSerializer.Serialize(Assert.Single(attempts, item => item.AttemptId == original.AttemptId)));
        }
        var retryAttempt = Assert.Single(attempts, item => item.AttemptId == retryAttemptId);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, retryAttempt.Status);
        Assert.Equal(originalExtractionId, retryAttempt.ResultExtractionId);
        await using var validationCommand = connection.CreateCommand();
        validationCommand.CommandText = "SELECT subject_extraction_id, outcome, report_path FROM extraction_validation_results WHERE attempt_id = $attempt";
        validationCommand.Parameters.AddWithValue("$attempt", retryAttemptId);
        await using (var reader = await validationCommand.ExecuteReaderAsync(ct))
        {
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(originalExtractionId, reader.GetString(0));
            Assert.Equal("Valid", reader.GetString(1));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reader.GetString(2), ct));
            Assert.Equal(retryAttemptId, report.RootElement.GetProperty("attemptId").GetString());
        }

        var noOp = fixture.Invoke("extract", "--json");
        Assert.Equal(0, noOp.ExitCode);
        using var noOpJson = JsonDocument.Parse(noOp.StandardOutput);
        var noOpData = noOpJson.RootElement.GetProperty("data");
        Assert.Equal(originalExtractionId, noOpData.GetProperty("extractionId").GetString());
        Assert.True(noOpData.GetProperty("authoritative").GetBoolean());
        Assert.True(noOpData.GetProperty("preferred").GetBoolean());
        Assert.True(noOpData.GetProperty("reusedExistingExtraction").GetBoolean());
        Assert.False(noOpData.GetProperty("processWasRun").GetBoolean());
        Assert.False(noOpData.GetProperty("validationWasRun").GetBoolean());
        Assert.Equal(JsonValueKind.Null, noOpData.GetProperty("attemptId").ValueKind);
        Assert.Equal(attempts.Count, (await repository.ListAttemptsAsync(buildId, ct)).Count);
    }

    [Fact]
    public async Task ExtractionsShow_UnknownId_ExitsOne()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        var result = fixture.Invoke("extractions", "show", new string('f', 64), "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        AssertFailureEnvelope(document.RootElement, "extractions show", 1);
        Assert.Equal(
            "HistoryEntryNotFound",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExtractAndExtractions_IssueNoHttpRequests()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        await fixture.SeedBuildAsync();

        // A custom-tool extraction never installs anything, so no request is ever served.
        var before = fixture.RequestCount;
        var extractionId = ExtractCustom(fixture);
        fixture.Invoke("extractions", "list", "--json");
        fixture.Invoke("extractions", "show", extractionId, "--json");
        fixture.Invoke("extractions", "promote", extractionId, "--json");

        Assert.Equal(0, before);
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task Phase1Through3JsonEnvelopes_RemainCompatible()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
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
    }

    private static string ExtractCustom(Phase4ExtractionCliFixture fixture)
    {
        var extract = fixture.Invoke(
            "extract", "--cpp2il-path", fixture.CustomToolPath, "--json");
        Assert.Equal(0, extract.ExitCode);
        using var document = JsonDocument.Parse(extract.StandardOutput);
        return document.RootElement.GetProperty("data").GetProperty("extractionId").GetString()!;
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

    private static void AssertFailureEnvelope(JsonElement root, string command, int expectedExitCode)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(expectedExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }
}
