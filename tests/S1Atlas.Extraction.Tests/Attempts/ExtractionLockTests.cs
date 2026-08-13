using S1Atlas.Extraction.Attempts;
using Xunit;

namespace S1Atlas.Extraction.Tests.Attempts;

public sealed class ExtractionLockTests : IDisposable
{
    private const string AttemptId = "0123456789abcdef0123456789abcdef";
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-lock-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = DateTimeOffset.Parse(
        "2026-08-12T12:00:00.0000000+00:00");

    [Fact]
    public async Task AcquireAsync_UsesCreateNewAndReportsLiveContenderAttempt()
    {
        Directory.CreateDirectory(_dataRoot);
        var first = CreateLock(ownerProcessId: 101, alive: id => id == 101);
        var second = CreateLock(ownerProcessId: 202, alive: id => id == 101);
        await using var lease = await first.AcquireAsync(
            AttemptId, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ExtractionAlreadyActiveException>(() =>
            second.AcquireAsync(
                "ffffffffffffffffffffffffffffffff",
                TestContext.Current.CancellationToken));

        Assert.Equal(AttemptId, exception.AttemptId);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "extraction.lock")));
    }

    [Fact]
    public async Task UpdateChildProcessIdAsync_AtomicallyReplacesStrictDocument()
    {
        Directory.CreateDirectory(_dataRoot);
        var manager = CreateLock(ownerProcessId: 101, alive: _ => false);
        await using var lease = await manager.AcquireAsync(
            AttemptId, TestContext.Current.CancellationToken);

        await lease.UpdateChildProcessIdAsync(303, TestContext.Current.CancellationToken);
        var state = await manager.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, state!.SchemaVersion);
        Assert.Equal(AttemptId, state.AttemptId);
        Assert.Equal(101, state.OwnerProcessId);
        Assert.Equal(303, state.ChildProcessId);
        Assert.Equal(_now, state.StartedAtUtc);
        Assert.Empty(Directory.EnumerateFiles(_dataRoot, "extraction.lock.*.tmp"));
    }

    [Fact]
    public async Task UpdateChildProcessIdAsync_OwnershipChanged_PreservesReplacement()
    {
        Directory.CreateDirectory(_dataRoot);
        var manager = CreateLock(ownerProcessId: 101, alive: _ => false);
        var lease = await manager.AcquireAsync(
            AttemptId, TestContext.Current.CancellationToken);
        var path = Path.Combine(_dataRoot, "extraction.lock");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "attemptId": "ffffffffffffffffffffffffffffffff",
              "ownerProcessId": 999,
              "childProcessId": null,
              "startedAtUtc": "2026-08-12T12:00:00.0000000+00:00"
            }
            """,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.UpdateChildProcessIdAsync(303, TestContext.Current.CancellationToken));

        var observed = await manager.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ffffffffffffffffffffffffffffffff", observed!.AttemptId);
        Assert.Equal(999, observed.OwnerProcessId);
        Assert.Null(observed.ChildProcessId);
    }

    [Fact]
    public async Task ReleaseAsync_OwnershipMismatchPreservesEvidence()
    {
        Directory.CreateDirectory(_dataRoot);
        var manager = CreateLock(ownerProcessId: 101, alive: _ => false);
        var lease = await manager.AcquireAsync(
            AttemptId, TestContext.Current.CancellationToken);
        var path = Path.Combine(_dataRoot, "extraction.lock");
        var json = await File.ReadAllTextAsync(
            path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path,
            json.Replace("\"ownerProcessId\": 101", "\"ownerProcessId\": 999", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.ReleaseAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AcquireAsync_DeadOwnerAndChild_ReclaimsStaleLock()
    {
        Directory.CreateDirectory(_dataRoot);
        await WriteLockAsync(ownerProcessId: 101, childProcessId: null);
        var manager = CreateLock(ownerProcessId: 202, alive: _ => false);

        await using var replacement = await manager.AcquireAsync(
            "ffffffffffffffffffffffffffffffff",
            TestContext.Current.CancellationToken);

        Assert.Equal(202, (await manager.ReadAsync(
            TestContext.Current.CancellationToken))!.OwnerProcessId);
    }

    [Fact]
    public async Task AcquireAsync_StaleOwnerButLiveChild_FailsClosed()
    {
        Directory.CreateDirectory(_dataRoot);
        await WriteLockAsync(ownerProcessId: 101, childProcessId: 303);

        var manager = CreateLock(ownerProcessId: 202, alive: id => id == 303);
        await Assert.ThrowsAsync<ExtractionAlreadyActiveException>(() =>
            manager.AcquireAsync(
                "ffffffffffffffffffffffffffffffff",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_MalformedLock_IsRejectedAndPreserved()
    {
        Directory.CreateDirectory(_dataRoot);
        var path = Path.Combine(_dataRoot, "extraction.lock");
        await File.WriteAllTextAsync(
            path, "{\"schemaVersion\":1,\"unknown\":true}",
            TestContext.Current.CancellationToken);
        var manager = CreateLock(ownerProcessId: 101, alive: _ => false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.ReadAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ReadAsync_InjectedReparsePointLock_IsRejected()
    {
        Directory.CreateDirectory(_dataRoot);
        var path = Path.Combine(_dataRoot, "extraction.lock");
        var inspectedLockPath = false;
        var manager = new ExtractionLock(
            _dataRoot,
            ownerProcessId: 101,
            _ => false,
            new FixedTimeProvider(_now),
            candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                {
                    inspectedLockPath = true;
                    return FileAttributes.ReparsePoint;
                }

                return File.GetAttributes(candidate);
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ReadAsync(TestContext.Current.CancellationToken));

        Assert.True(inspectedLockPath);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AcquireAsync_PreCanceled_DoesNotCreateLock()
    {
        Directory.CreateDirectory(_dataRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateLock(ownerProcessId: 101, alive: _ => false)
                .AcquireAsync(AttemptId, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_dataRoot, "extraction.lock")));
    }

    [Fact]
    public async Task DisposeAfterRelease_IsIdempotent()
    {
        Directory.CreateDirectory(_dataRoot);
        var lease = await CreateLock(ownerProcessId: 101, alive: _ => false)
            .AcquireAsync(AttemptId, TestContext.Current.CancellationToken);

        await lease.ReleaseAsync(TestContext.Current.CancellationToken);
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.False(File.Exists(Path.Combine(_dataRoot, "extraction.lock")));
    }

    private ExtractionLock CreateLock(int ownerProcessId, Func<int, bool> alive) =>
        new(_dataRoot, ownerProcessId, alive, new FixedTimeProvider(_now));

    private Task WriteLockAsync(int ownerProcessId, int? childProcessId) =>
        File.WriteAllTextAsync(
            Path.Combine(_dataRoot, "extraction.lock"),
            $$"""
            {
              "schemaVersion": 1,
              "attemptId": "{{AttemptId}}",
              "ownerProcessId": {{ownerProcessId}},
              "childProcessId": {{(childProcessId is null ? "null" : childProcessId.Value.ToString())}},
              "startedAtUtc": "2026-08-12T12:00:00.0000000+00:00"
            }
            """,
            TestContext.Current.CancellationToken);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
