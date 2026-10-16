using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Extraction;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Extraction;

public sealed class ExtractionsListBuildPrefixTests : IAsyncDisposable
{
    private static readonly string BuildA = "abcdef12" + new string('0', 56);
    private static readonly string BuildA2 = "abcdef12" + new string('1', 56);
    private static readonly string BuildB = "12345678" + new string('0', 56);

    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-16T00:00:00Z");

    private const string ToolInstanceId = "build-prefix-tool";
    private const string RecipeA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RecipeB = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RecipeC = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string ProfileDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PolicyDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "s1atlas-extractions-build-prefix-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public ExtractionsListBuildPrefixTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        _repository.Dispose();
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }

    [Fact]
    public async Task List_BuildUniquePrefix_ResolvesToFullBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var (extractionA, _) = await SeedTwoBuildsAsync(ct);

        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", "abcdef12", "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var entries = json.RootElement.GetProperty("data").GetProperty("entries")
            .EnumerateArray().ToArray();
        var single = Assert.Single(entries);
        Assert.Equal(extractionA, single.GetProperty("id").GetString());
        Assert.Equal(BuildA, single.GetProperty("buildId").GetString());
    }

    [Fact]
    public async Task List_BuildUniquePrefix_AcceptsUppercase()
    {
        var ct = TestContext.Current.CancellationToken;
        var (extractionA, _) = await SeedTwoBuildsAsync(ct);

        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", "ABCDEF12", "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var entries = json.RootElement.GetProperty("data").GetProperty("entries")
            .EnumerateArray().ToArray();
        var single = Assert.Single(entries);
        Assert.Equal(extractionA, single.GetProperty("id").GetString());
        Assert.Equal(BuildA, single.GetProperty("buildId").GetString());
    }

    [Fact]
    public async Task List_BuildFullId_IsCaseInsensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var (extractionA, _) = await SeedTwoBuildsAsync(ct);

        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", BuildA.ToUpperInvariant(), "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var entries = json.RootElement.GetProperty("data").GetProperty("entries")
            .EnumerateArray().ToArray();
        var single = Assert.Single(entries);
        Assert.Equal(extractionA, single.GetProperty("id").GetString());
        Assert.Equal(BuildA, single.GetProperty("buildId").GetString());
    }

    [Fact]
    public async Task List_BuildAbsentAndBlank_ListsEverythingUnfiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (extractionA, extractionB) = await SeedTwoBuildsAsync(ct);

        var absent = Invoke(ct, "extractions", "list", "--json");
        Assert.Equal(0, absent.ExitCode);
        Assert.Equal(string.Empty, absent.Stderr);
        using (var json = JsonDocument.Parse(absent.Stdout))
        {
            AssertBothBuildsPresent(json, extractionA, extractionB);
        }

        var blank = Invoke(ct, "extractions", "list", "--build", "", "--json");
        Assert.Equal(0, blank.ExitCode);
        Assert.Equal(string.Empty, blank.Stderr);
        using (var json = JsonDocument.Parse(blank.Stdout))
        {
            AssertBothBuildsPresent(json, extractionA, extractionB);
        }
    }

    [Fact]
    public async Task List_BuildAmbiguousPrefix_Exits1ListingMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAmbiguousBuildsAsync(ct);

        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", "abcdef12", "--json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);
        string message;
        using (var json = JsonDocument.Parse(stdout))
        {
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("AmbiguousBuildPrefix", error.GetProperty("code").GetString());
            message = error.GetProperty("message").GetString()!;
            Assert.Contains("matches 2 builds", message, StringComparison.Ordinal);
            Assert.Contains("abcdef120000 (first seen", message, StringComparison.Ordinal);
            Assert.Contains("abcdef121111 (first seen", message, StringComparison.Ordinal);
            Assert.DoesNotContain(BuildA, message, StringComparison.Ordinal);
            Assert.DoesNotContain(BuildA2, message, StringComparison.Ordinal);
        }

        var human = Invoke(ct, "extractions", "list", "--build", "abcdef12");
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(string.Empty, human.Stdout);
        Assert.Contains(message, human.Stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    AmbiguousBuildPrefix", human.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_BuildUnknownPrefix_Exits1BuildNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoBuildsAsync(ct);

        const string prefix = "deadbeef";
        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", prefix, "--json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);
        using (var json = JsonDocument.Parse(stdout))
        {
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
            Assert.Equal($"Build '{prefix}' not found.", error.GetProperty("message").GetString());
        }

        var human = Invoke(ct, "extractions", "list", "--build", prefix);
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(string.Empty, human.Stdout);
        Assert.Contains($"Build '{prefix}' not found.", human.Stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    BuildNotFound", human.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_BuildFullId_UnknownExits1BuildNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoBuildsAsync(ct);

        var unknown = new string('e', 64);
        var truncated = unknown[..8] + "..." + unknown[^8..];
        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", unknown, "--json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);
        using (var json = JsonDocument.Parse(stdout))
        {
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            Assert.Contains(truncated, message, StringComparison.Ordinal);
            Assert.DoesNotContain(unknown, message, StringComparison.Ordinal);
        }

        var human = Invoke(ct, "extractions", "list", "--build", unknown);
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(string.Empty, human.Stdout);
        Assert.Contains(truncated, human.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown, human.Stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    BuildNotFound", human.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_BuildMalformedInput_Exits1BuildNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoBuildsAsync(ct);

        foreach (var input in new[] { "zzzzzzzz", "abc" })
        {
            var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", input, "--json");

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stderr);
            using (var json = JsonDocument.Parse(stdout))
            {
                var error = json.RootElement.GetProperty("error");
                Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
                Assert.Equal($"Build '{input}' not found.", error.GetProperty("message").GetString());
            }

            var human = Invoke(ct, "extractions", "list", "--build", input);
            Assert.Equal(1, human.ExitCode);
            Assert.Equal(string.Empty, human.Stdout);
            Assert.Contains($"Build '{input}' not found.", human.Stderr, StringComparison.Ordinal);
            Assert.Contains("Code:    BuildNotFound", human.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task List_BuildPrefix_WithIncludeFailed_ScopesAttemptsToBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);
        await ExtractionSeed.SeedToolInstanceAsync(_dataDirectory, ToolInstanceId, ct);

        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildA, BaseTime), ct);
        var seededA = await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildA, RecipeA,
            ToolInstanceId, ProfileDigest, PolicyDigest, BaseTime, ct);

        var later = BaseTime.AddHours(1);
        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildB, later), ct);
        await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildB, RecipeB,
            ToolInstanceId, ProfileDigest, PolicyDigest, later, ct);

        var attemptTime = BaseTime.AddMinutes(20);
        var validating = await ExtractionSeed.AdvanceAttemptToValidatingAsync(
            _repository, BuildA, RecipeA,
            Guid.NewGuid().ToString("N"),
            seededA.InputSnapshot.InputSnapshotId,
            ToolInstanceId, ProfileDigest, PolicyDigest, attemptTime, ct);
        var failed = validating with
        {
            Status = ExtractionAttemptStatus.Failed,
            CompletedAtUtc = attemptTime.AddMinutes(1),
            FailureStage = ExtractionFailureStage.ProcessExecution,
            FailureCode = ExtractionFailureCode.ProcessExitNonZero,
            FailureMessage = "The process exited non-zero."
        };
        await _repository.TransitionAttemptAsync(failed, ExtractionAttemptStatus.Validating, ct);

        var (exitCode, stdout, stderr) = Invoke(
            ct, "extractions", "list", "--build", "abcdef12", "--include-failed", "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var entries = json.RootElement.GetProperty("data").GetProperty("entries")
            .EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(BuildA, entry.GetProperty("buildId").GetString()));
        Assert.Contains(entries, entry => entry.GetProperty("kind").GetString() == "Attempt");
    }

    [Fact]
    public async Task List_BuildPrefix_NoKnownBuilds_Exits1BuildNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (exitCode, stdout, stderr) = Invoke(ct, "extractions", "list", "--build", "abcdef12", "--json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);
        using (var json = JsonDocument.Parse(stdout))
        {
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("BuildNotFound", error.GetProperty("code").GetString());
            Assert.Equal("Build 'abcdef12' not found.", error.GetProperty("message").GetString());
        }

        var human = Invoke(ct, "extractions", "list", "--build", "abcdef12");
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(string.Empty, human.Stdout);
        Assert.Contains("Build 'abcdef12' not found.", human.Stderr, StringComparison.Ordinal);
        Assert.Contains("Code:    BuildNotFound", human.Stderr, StringComparison.Ordinal);
    }

    private async Task SeedAmbiguousBuildsAsync(CancellationToken ct)
    {
        await _repository.InitializeAsync(ct);
        await ExtractionSeed.SeedToolInstanceAsync(_dataDirectory, ToolInstanceId, ct);

        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildA, BaseTime), ct);
        await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildA, RecipeA,
            ToolInstanceId, ProfileDigest, PolicyDigest, BaseTime, ct);

        var second = BaseTime.AddHours(1);
        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildA2, second), ct);
        await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildA2, RecipeC,
            ToolInstanceId, ProfileDigest, PolicyDigest, second, ct);

        var third = BaseTime.AddHours(2);
        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildB, third), ct);
        await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildB, RecipeB,
            ToolInstanceId, ProfileDigest, PolicyDigest, third, ct);
    }

    private static void AssertBothBuildsPresent(JsonDocument json, string extractionA, string extractionB)
    {
        var entries = json.RootElement.GetProperty("data").GetProperty("entries")
            .EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        var ids = entries.Select(entry => entry.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(extractionA, ids);
        Assert.Contains(extractionB, ids);
        var builds = entries.Select(entry => entry.GetProperty("buildId").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(BuildA, builds);
        Assert.Contains(BuildB, builds);
    }

    private async Task<(string ExtractionA, string ExtractionB)> SeedTwoBuildsAsync(CancellationToken ct)
    {
        await _repository.InitializeAsync(ct);
        await ExtractionSeed.SeedToolInstanceAsync(_dataDirectory, ToolInstanceId, ct);

        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildA, BaseTime), ct);
        var seededA = await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildA, RecipeA,
            ToolInstanceId, ProfileDigest, PolicyDigest, BaseTime, ct);

        var later = BaseTime.AddHours(1);
        await _repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot(BuildB, later), ct);
        var seededB = await ExtractionSeed.SeedValidatedExtractionAsync(
            _repository, _dataDirectory, BuildB, RecipeB,
            ToolInstanceId, ProfileDigest, PolicyDigest, later, ct);

        return (seededA.Extraction.ExtractionId, seededB.Extraction.ExtractionId);
    }

    private (int ExitCode, string Stdout, string Stderr) Invoke(CancellationToken ct, params string[] args)
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(args, output, error, ct);
        return (exitCode, output.ToString(), error.ToString());
    }
}
