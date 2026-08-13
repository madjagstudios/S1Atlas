using System.Text.Json;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Attempts;
using Xunit;

namespace S1Atlas.Extraction.Tests.Attempts;

public sealed class AttemptDocumentStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-attempt-documents-{Guid.NewGuid():N}");

    [Fact]
    public void Create_ReturnsOnlyTheSpecifiedOwnedAttemptPaths()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = OwnedAttemptPaths.Create(
            _dataRoot, "build-a", "0123456789abcdef0123456789abcdef");
        var buildRoot = Path.Combine(_dataRoot, "builds", "build-a");
        var staging = Path.Combine(
            buildRoot, "extractions", ".staging", paths.AttemptId);
        var attempt = Path.Combine(buildRoot, "attempts", paths.AttemptId);

        Assert.Equal(staging, paths.StagingRoot);
        Assert.Equal(Path.Combine(staging, "working"), paths.WorkingRoot);
        Assert.Equal(Path.Combine(staging, "output"), paths.OutputRoot);
        Assert.Equal(Path.Combine(staging, "logs"), paths.StagingLogsRoot);
        Assert.Equal(attempt, paths.AttemptRoot);
        Assert.Equal(Path.Combine(attempt, "attempt.json"), paths.AttemptDocumentPath);
        Assert.Equal(Path.Combine(attempt, "logs"), paths.FinalLogsRoot);
        Assert.Equal(Path.Combine(attempt, "candidate-output"), paths.CandidateOutputRoot);
        Assert.Equal(Path.Combine(attempt, "retained-output"), paths.RetainedOutputRoot);
    }

    [Theory]
    [InlineData("../escape", "0123456789abcdef0123456789abcdef")]
    [InlineData("BUILD/child", "0123456789abcdef0123456789abcdef")]
    [InlineData("build-a", "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("build-a", "..\\escape")]
    [InlineData("build-a", "0123456789abcdef0123456789abcde")]
    public void Create_UnsafeSegments_AreRejected(string buildId, string attemptId)
    {
        Directory.CreateDirectory(_dataRoot);
        Assert.Throws<ArgumentException>(() =>
            OwnedAttemptPaths.Create(_dataRoot, buildId, attemptId));
    }

    [Fact]
    public void Create_FileAsAncestor_IsRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "builds"), "not-a-directory");

        Assert.Throws<InvalidOperationException>(() => OwnedAttemptPaths.Create(
            _dataRoot, "build-a", "0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void Create_InjectedReparsePointAncestor_IsRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        var reparsePath = Path.Combine(_dataRoot, "builds");
        var inspectedReparsePath = false;

        Assert.Throws<InvalidOperationException>(() => OwnedAttemptPaths.Create(
            _dataRoot,
            "build-a",
            "0123456789abcdef0123456789abcdef",
            path =>
            {
                if (string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase))
                {
                    inspectedReparsePath = true;
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }

                return File.GetAttributes(path);
            }));
        Assert.True(inspectedReparsePath);
    }

    [Fact]
    public async Task WriteAsync_PersistsStrictVersionOneAndExactTypedFactsAtomically()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = OwnedAttemptPaths.Create(
            _dataRoot, "build-a", "0123456789abcdef0123456789abcdef");
        var attempt = CreateAttempt(paths);
        var facts = CreateFacts();
        string? observedTemporary = null;
        var store = new AttemptDocumentStore((temporary, destination) =>
        {
            observedTemporary = temporary;
            Assert.Equal(paths.AttemptDocumentPath, destination);
            Assert.True(File.Exists(temporary));
            Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(temporary));
            Assert.Matches(@"attempt\.json\.[0-9a-f]{32}\.tmp$", temporary);
        });

        await store.WriteAsync(paths, attempt, facts, TestContext.Current.CancellationToken);
        var read = await store.TryReadAsync(
            paths, attempt, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(
            paths.AttemptDocumentPath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(observedTemporary);
        Assert.False(File.Exists(observedTemporary));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(attempt.AttemptId, document.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(attempt.RecipeId, document.RootElement.GetProperty("recipeId").GetString());
        Assert.Equal(attempt.ToolInstanceId, document.RootElement.GetProperty("toolInstanceId").GetString());
        Assert.Equal(attempt.ProfileDigest, document.RootElement.GetProperty("profileDigest").GetString());
        Assert.Equal(attempt.ValidationPolicyDigest, document.RootElement.GetProperty("validationPolicyDigest").GetString());
        Assert.Equal(["--one=a b", "--literal=&()$`"],
            document.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("true", document.RootElement.GetProperty("environmentOverrides").GetProperty("NO_COLOR").GetString());
        Assert.Equal(37, document.RootElement.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(144L, document.RootElement.GetProperty("standardOutputDiscardedBytes").GetInt64());
        Assert.Equal(3, document.RootElement.GetProperty("discardedFileCount").GetInt32());
        Assert.Equal(facts.OwnerProcessId, read!.ExecutionFacts!.OwnerProcessId);
        Assert.Equal(facts.ToolTrust, read.ExecutionFacts.ToolTrust);
        Assert.Equal(facts.ResolvedInputPaths, read.ExecutionFacts.ResolvedInputPaths);
        Assert.Equal(facts.Arguments, read.ExecutionFacts.Arguments);
        Assert.Equal(facts.EnvironmentOverrides, read.ExecutionFacts.EnvironmentOverrides);
        Assert.Equal(facts.PreInputManifest!.Entries, read.ExecutionFacts.PreInputManifest!.Entries);
        Assert.Equal(facts.PostInputManifest!.Entries, read.ExecutionFacts.PostInputManifest!.Entries);
        Assert.Equal(facts.Timeout, read.ExecutionFacts.Timeout);
        Assert.Equal(attempt, read.Attempt);
    }

    [Fact]
    public async Task TryReadAsync_MalformedUnknownOrMismatchedDocuments_AreRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = OwnedAttemptPaths.Create(
            _dataRoot, "build-a", "0123456789abcdef0123456789abcdef");
        var attempt = CreateAttempt(paths);
        var store = new AttemptDocumentStore();
        await store.WriteAsync(paths, attempt, CreateFacts(), TestContext.Current.CancellationToken);
        var valid = await File.ReadAllTextAsync(
            paths.AttemptDocumentPath, TestContext.Current.CancellationToken);

        foreach (var invalid in new[]
        {
            valid[..^1] + ",\"unknown\":true}",
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal),
            valid.Replace(
                "  \"toolTrust\": \"managedPinned\"," + Environment.NewLine,
                string.Empty,
                StringComparison.Ordinal),
            valid.Replace(attempt.BuildId, "other-build", StringComparison.Ordinal),
            "{ malformed"
        })
        {
            await File.WriteAllTextAsync(
                paths.AttemptDocumentPath, invalid, TestContext.Current.CancellationToken);
            Assert.Null(await store.TryReadAsync(
                paths, attempt, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task WriteAsync_HumanFailureMessage_DropsRawStackLines()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = OwnedAttemptPaths.Create(
            _dataRoot, "build-a", "0123456789abcdef0123456789abcdef");
        var attempt = CreateAttempt(paths) with
        {
            Status = ExtractionAttemptStatus.Failed,
            CompletedAtUtc = DateTimeOffset.Parse("2026-08-12T12:05:00Z"),
            FailureStage = ExtractionFailureStage.ProcessExecution,
            FailureCode = ExtractionFailureCode.ProcessExitNonZero,
            FailureMessage = "Cpp2IL exited unexpectedly.\r\n   at Secret.Namespace.Run()"
        };

        await new AttemptDocumentStore().WriteAsync(
            paths, attempt, CreateFacts(), TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(
            paths.AttemptDocumentPath, TestContext.Current.CancellationToken);

        Assert.Contains("Cpp2IL exited unexpectedly.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret.Namespace", json, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", json, StringComparison.Ordinal);
    }

    private static ExtractionAttempt CreateAttempt(OwnedAttemptPaths paths) => new(
        paths.AttemptId, new string('1', 64), "build-a", "tool-1", "profile", 2,
        new string('2', 64), "policy", 3, new string('3', 64), 1, 1,
        ExtractionInputSource.Live, null, ExtractionAttemptStatus.Running,
        DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
        DateTimeOffset.Parse("2026-08-12T12:01:00Z"), null,
        new string('4', 64), new string('5', 64), paths.WorkingRoot,
        Path.Combine(paths.FinalLogsRoot, "stdout.log"),
        Path.Combine(paths.FinalLogsRoot, "stderr.log"), true, false, 144, 0,
        2468, 0, null, null, null, true, 3, 99, null, null);

    internal static AttemptExecutionFacts CreateFacts() => new(
        OwnerProcessId: 1357,
        ToolTrust: "managedPinned",
        ResolvedInputPaths: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gameAssembly"] = @"C:\game root\GameAssembly.dll",
            ["globalMetadata"] = @"C:\game root\global-metadata.dat"
        },
        Arguments: ["--one=a b", "--literal=&()$`"],
        EnvironmentOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["NO_COLOR"] = "true",
            ["REMOVED"] = null
        },
        PreInputManifest: CreateManifest('a'),
        PostInputManifest: CreateManifest('b'),
        Timeout: TimeSpan.FromSeconds(37));

    private static InputManifest CreateManifest(char hash) => new(
        [new InputManifestEntry(
            "GameAssembly.dll", "gameAssembly", 123, new string(hash, 64),
            DateTimeOffset.Parse("2026-08-12T11:00:00Z"))]);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
