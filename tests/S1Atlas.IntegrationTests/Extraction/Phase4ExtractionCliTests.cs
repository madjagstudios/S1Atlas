using System.Text.Json;
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
