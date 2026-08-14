using System.Text.Json;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

public sealed class Phase5ArchivedReplayCliTests
{
    [Fact]
    public async Task Extract_InputSnapshotOption_ValidatesGrammarAndConstraints()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        var validId = new string('a', 64);

        var badHex = fixture.Invoke("extract", "--input-snapshot", "not-hex", "--json");
        var missingRetry = fixture.Invoke("extract", "--input-snapshot", validId, "--json");
        var conflict = fixture.Invoke(
            "extract", "--input-snapshot", validId, "--retry", "--snapshot-inputs", "--json");

        Assert.Equal("InvalidInputSnapshot", ErrorCode(badHex.StandardOutput));
        Assert.Equal("InputSnapshotRequiresRetry", ErrorCode(missingRetry.StandardOutput));
        Assert.Equal("InputSnapshotConflict", ErrorCode(conflict.StandardOutput));
        Assert.Equal(1, badHex.ExitCode);
        Assert.Equal(1, missingRetry.ExitCode);
        Assert.Equal(1, conflict.ExitCode);
    }

    [Fact]
    public async Task Extract_LiveRetryThenArchivedReplay_CertifiesSnapshot()
    {
        await using var fixture = new Phase4ExtractionCliFixture(
            new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
        fixture.InstallManagedTool();
        var buildId = await fixture.SeedBuildAsync();

        // A real live retry that archives its verified inputs: a snapshot is created but
        // never replay-certified because the process ran from live input.
        var liveRetry = fixture.Invoke("extract", "--snapshot-inputs", "--retry", "--json");
        Assert.Equal(0, liveRetry.ExitCode);
        var liveData = JsonDocument.Parse(liveRetry.StandardOutput).RootElement.GetProperty("data");
        Assert.Equal("Live", liveData.GetProperty("inputSource").GetString());
        Assert.False(liveData.GetProperty("inputSnapshotReplayVerified").GetBoolean());
        var snapshotId = liveData.GetProperty("inputSnapshotId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(snapshotId));

        var repository = await fixture.OpenRepositoryAsync();
        var beforeReplay = await repository.GetInputSnapshotAsync(
            snapshotId!, TestContext.Current.CancellationToken);
        Assert.False(beforeReplay!.ReplayVerified);

        // An explicit archived-only retry runs Cpp2IL from the stored snapshot and
        // certifies it replay-verified after an authoritative validated extraction.
        var archivedRetry = fixture.Invoke(
            "extract", "--build", buildId, "--input-snapshot", snapshotId!, "--retry", "--json");
        Assert.Equal(0, archivedRetry.ExitCode);
        var archivedData = JsonDocument.Parse(archivedRetry.StandardOutput)
            .RootElement.GetProperty("data");
        Assert.Equal("ArchivedSnapshot", archivedData.GetProperty("inputSource").GetString());
        Assert.Equal(snapshotId, archivedData.GetProperty("inputSnapshotId").GetString());
        Assert.True(archivedData.GetProperty("inputSnapshotReplayVerified").GetBoolean());

        var afterReplay = await repository.GetInputSnapshotAsync(
            snapshotId!, TestContext.Current.CancellationToken);
        Assert.True(afterReplay!.ReplayVerified);
        Assert.NotNull(afterReplay.ReplayVerifiedAtUtc);
    }

    private static string? ErrorCode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
