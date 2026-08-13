using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Validation;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class CandidateOutputInspectorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-candidate-inspector-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAsync_NormalNestedTree_ReturnsEveryFileSortedByNormalizedRelativePath()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(Path.Combine(paths.CandidateOutputRoot, "sub", "nested"));
        File.WriteAllBytes(Path.Combine(paths.CandidateOutputRoot, "b.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(paths.CandidateOutputRoot, "a.dll"), [4, 5]);
        File.WriteAllBytes(Path.Combine(paths.CandidateOutputRoot, "sub", "c.dll"), [6]);
        File.WriteAllBytes(
            Path.Combine(paths.CandidateOutputRoot, "sub", "nested", "d.dll"), [7, 8, 9, 10]);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        Assert.True(result.ContainmentPassed);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Inventory);
        Assert.Equal(
            [
                "reconstructed/a.dll",
                "reconstructed/b.dll",
                "reconstructed/sub/c.dll",
                "reconstructed/sub/nested/d.dll"
            ],
            result.Inventory!.Artifacts.Select(artifact => artifact.RelativePath));
        Assert.Equal(10, result.Inventory.TotalBytes);
    }

    [Fact]
    public async Task InspectAsync_UsesReconstructedPrefixInFinalRelativePaths()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        File.WriteAllBytes(Path.Combine(paths.CandidateOutputRoot, "Assembly-CSharp.dll"), [1]);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        Assert.True(result.ContainmentPassed);
        var artifact = Assert.Single(result.Inventory!.Artifacts);
        Assert.Equal("reconstructed/Assembly-CSharp.dll", artifact.RelativePath);
        Assert.StartsWith("reconstructed/", artifact.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_EmptyTree_ReturnsSuccessfulEmptyInventoryForPolicyEvaluation()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        Assert.True(result.ContainmentPassed);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Inventory);
        Assert.Empty(result.Inventory!.Artifacts);
        Assert.Equal(0, result.Inventory.TotalBytes);
    }

    [Fact]
    public async Task InspectAsync_RootOutsideExpectedAttemptPath_ReturnsInvalidContainment()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"s1atlas-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var attempt = CreateAttempt(
                paths, ExtractionAttemptStatus.ProcessCompleted, outsideRoot);
            var inspector = new CandidateOutputInspector(new Sha256FileHasher());

            var result = await inspector.InspectAsync(
                attempt, _dataRoot, TestContext.Current.CancellationToken);

            AssertInvalidContainment(result);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAsync_CandidateFieldDoesNotEqualOwnedPath_ReturnsInvalidContainment()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var wrongPath = Path.Combine(paths.AttemptRoot, "wrong-output");
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, wrongPath);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_ReparseDirectory_ReturnsInvalidWithoutFollowing()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var target = Path.Combine(_dataRoot, "reparse-target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "hidden.dll"), [1, 2, 3]);
        var linkPath = Path.Combine(paths.CandidateOutputRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or
            PlatformNotSupportedException)
        {
            return;
        }

        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_ReparseFile_ReturnsInvalidWithoutFollowing()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var target = Path.Combine(_dataRoot, "reparse-target.dll");
        File.WriteAllBytes(target, [1, 2, 3]);
        var linkPath = Path.Combine(paths.CandidateOutputRoot, "linked.dll");
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or
            PlatformNotSupportedException)
        {
            return;
        }

        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_CaseInsensitiveDuplicatePaths_ReturnsInvalid()
    {
        var paths = CreatePaths();
        var candidateRoot = paths.CandidateOutputRoot;
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, candidateRoot);
        var inspector = new CandidateOutputInspector(
            new ConstantHasher(),
            directoryExists: _ => true,
            getFiles: directory => string.Equals(
                directory, candidateRoot, StringComparison.Ordinal)
                ? [
                    Path.Combine(candidateRoot, "Foo.txt"),
                    Path.Combine(candidateRoot, "foo.txt")
                  ]
                : [],
            getDirectories: _ => [],
            getAttributes: _ => FileAttributes.Normal,
            observeFile: _ => new CandidateFileObservation(1, DateTimeOffset.UnixEpoch));

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_FileChangesDuringObservation_ReturnsInvalid()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var target = Path.Combine(paths.CandidateOutputRoot, "mutating.dll");
        File.WriteAllBytes(target, [1, 2, 3]);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new MutateAfterHashHasher(target));

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_UnreadableFile_ReturnsInvalidWithoutPartialInventory()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        File.WriteAllBytes(Path.Combine(paths.CandidateOutputRoot, "a.dll"), [1]);
        var unreadable = Path.Combine(paths.CandidateOutputRoot, "b.dll");
        File.WriteAllBytes(unreadable, [2]);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new ThrowingHasher(unreadable));

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_OverflowingCountsOrBytes_ReturnsInvalid()
    {
        var paths = CreatePaths();
        var candidateRoot = paths.CandidateOutputRoot;
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, candidateRoot);
        var file1 = Path.Combine(candidateRoot, "big1.bin");
        var file2 = Path.Combine(candidateRoot, "big2.bin");
        var inspector = new CandidateOutputInspector(
            new ConstantHasher(),
            directoryExists: _ => true,
            getFiles: directory => string.Equals(
                directory, candidateRoot, StringComparison.Ordinal) ? [file1, file2] : [],
            getDirectories: _ => [],
            getAttributes: _ => FileAttributes.Normal,
            observeFile: _ => new CandidateFileObservation(long.MaxValue, DateTimeOffset.UnixEpoch));

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_AttemptStatusNotInventoriable_ReturnsInvalidContainment()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.Succeeded, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_MissingCandidateOutputPath_ReturnsInvalidContainment()
    {
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Validating, null);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        AssertInvalidContainment(result);
    }

    [Fact]
    public async Task InspectAsync_ValidatingStatusWithCandidatePath_IsInventoriable()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.Validating, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());

        var result = await inspector.InspectAsync(
            attempt, _dataRoot, TestContext.Current.CancellationToken);

        Assert.True(result.ContainmentPassed);
    }

    [Fact]
    public async Task InspectAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.CandidateOutputRoot);
        var attempt = CreateAttempt(
            paths, ExtractionAttemptStatus.ProcessCompleted, paths.CandidateOutputRoot);
        var inspector = new CandidateOutputInspector(new Sha256FileHasher());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.InspectAsync(attempt, _dataRoot, cancellation.Token));
    }

    private static void AssertInvalidContainment(CandidateInspectionResult result)
    {
        Assert.False(result.ContainmentPassed);
        Assert.Null(result.Inventory);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.True(issue.PreferenceBlocking);
    }

    private OwnedAttemptPaths CreatePaths(string buildId = "build-a", string? attemptId = null)
    {
        Directory.CreateDirectory(_dataRoot);
        return OwnedAttemptPaths.Create(
            _dataRoot, buildId, attemptId ?? "0123456789abcdef0123456789abcdef");
    }

    private static ExtractionAttempt CreateAttempt(
        OwnedAttemptPaths paths,
        ExtractionAttemptStatus status,
        string? candidateOutputPath) => new(
        AttemptId: paths.AttemptId,
        RecipeId: new string('1', 64),
        BuildId: paths.BuildId,
        ToolInstanceId: "tool-1",
        ProfileId: "profile",
        ProfileVersion: 1,
        ProfileDigest: new string('2', 64),
        ValidationPolicyId: "policy",
        ValidationPolicyVersion: 1,
        ValidationPolicyDigest: new string('3', 64),
        AdapterVersion: 1,
        ExtractionSchemaVersion: 1,
        InputSource: ExtractionInputSource.Live,
        InputSnapshotId: null,
        Status: status,
        CreatedAtUtc: DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
        StartedAtUtc: DateTimeOffset.Parse("2026-08-12T12:01:00Z"),
        CompletedAtUtc: DateTimeOffset.Parse("2026-08-12T12:05:00Z"),
        PreInputManifestDigest: new string('4', 64),
        PostInputManifestDigest: new string('5', 64),
        WorkingPath: paths.WorkingRoot,
        StandardOutputPath: Path.Combine(paths.FinalLogsRoot, "stdout.log"),
        StandardErrorPath: Path.Combine(paths.FinalLogsRoot, "stderr.log"),
        StandardOutputTruncated: false,
        StandardErrorTruncated: false,
        StandardOutputDiscardedBytes: 0,
        StandardErrorDiscardedBytes: 0,
        ProcessId: 1234,
        ProcessExitCode: 0,
        FailureStage: null,
        FailureCode: null,
        FailureMessage: null,
        KeepFailedArtifacts: true,
        DiscardedFileCount: 0,
        DiscardedByteCount: 0,
        CandidateOutputPath: candidateOutputPath,
        ResultExtractionId: null);

    private sealed class ConstantHasher : IFileHasher
    {
        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new string('a', 64));
    }

    private sealed class ThrowingHasher(string target) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated unreadable candidate file.");
            }

            return await _inner.ComputeSha256Async(path, cancellationToken);
        }
    }

    private sealed class MutateAfterHashHasher(string target) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        private bool _mutated;

        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            if (!_mutated && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                _mutated = true;
                await File.WriteAllBytesAsync(target, [0xde, 0xad, 0xbe, 0xef], cancellationToken);
            }

            return hash;
        }
    }
}
