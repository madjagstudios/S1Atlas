using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionCleanupModelsTests
{
    private static ExtractionCleanupItem Item(
        string id,
        int fileCount,
        long byteCount,
        ExtractionCleanupItemKind kind = ExtractionCleanupItemKind.TerminalAttempt) =>
        new(
            kind,
            id,
            BuildId: "build-" + id,
            AttemptId: "attempt-" + id,
            DisplayPath: "path/" + id,
            ControllingTimestampUtc: new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            FileCount: fileCount,
            ByteCount: byteCount);

    private static ExtractionCleanupBlockedItem Blocked(string id) =>
        new(
            ExtractionCleanupItemKind.ExtractionStaging,
            id,
            DisplayPath: "blocked/" + id,
            Code: "EvidenceChanged",
            Message: "blocked " + id);

    private static ExtractionCleanupFailure Failure(string id) =>
        new(
            ExtractionCleanupItemKind.TerminalAttempt,
            id,
            Code: "DatabaseDeleteFailed",
            Message: "failure " + id);

    private static ExtractionCleanupPlan Plan(
        IReadOnlyList<ExtractionCleanupItem>? eligible = null,
        IReadOnlyList<ExtractionCleanupBlockedItem>? blocked = null) =>
        new(
            OlderThan: TimeSpan.FromDays(30),
            CutoffUtc: new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            EligibleItems: eligible ?? Array.Empty<ExtractionCleanupItem>(),
            BlockedItems: blocked ?? Array.Empty<ExtractionCleanupBlockedItem>());

    [Fact]
    public void EligibleAggregates_SumOnlyEligibleItems()
    {
        var plan = Plan(
            eligible: new[] { Item("a", 3, 100), Item("b", 5, 250) },
            blocked: new[] { Blocked("c") });

        Assert.Equal(8, plan.EligibleFileCount);
        Assert.Equal(350, plan.EligibleByteCount);
    }

    [Fact]
    public void EligibleAggregates_AreZeroWhenNoEligibleItems()
    {
        var plan = Plan(blocked: new[] { Blocked("c") });

        Assert.Equal(0, plan.EligibleFileCount);
        Assert.Equal(0, plan.EligibleByteCount);
    }

    [Fact]
    public void HasOperationalProblems_IsFalse_OnlyWhenBlockedAndFailuresBothEmpty()
    {
        var result = new ExtractionCleanupResult(
            Plan(eligible: new[] { Item("a", 1, 1) }),
            Applied: true,
            DeletedItems: new[] { Item("a", 1, 1) },
            Failures: Array.Empty<ExtractionCleanupFailure>());

        Assert.False(result.HasOperationalProblems);
    }

    [Fact]
    public void HasOperationalProblems_IsTrue_WhenBlockedItemsRemain()
    {
        var result = new ExtractionCleanupResult(
            Plan(blocked: new[] { Blocked("c") }),
            Applied: true,
            DeletedItems: Array.Empty<ExtractionCleanupItem>(),
            Failures: Array.Empty<ExtractionCleanupFailure>());

        Assert.True(result.HasOperationalProblems);
    }

    [Fact]
    public void HasOperationalProblems_IsTrue_WhenFailuresRemain()
    {
        var result = new ExtractionCleanupResult(
            Plan(),
            Applied: true,
            DeletedItems: Array.Empty<ExtractionCleanupItem>(),
            Failures: new[] { Failure("f") });

        Assert.True(result.HasOperationalProblems);
    }

    [Fact]
    public void Plan_PreservesDeterministicCallerOrder()
    {
        var eligible = new[] { Item("z", 1, 1), Item("a", 1, 1), Item("m", 1, 1) };
        var blocked = new[] { Blocked("z"), Blocked("a") };

        var plan = Plan(eligible, blocked);

        Assert.Equal(new[] { "z", "a", "m" }, plan.EligibleItems.Select(i => i.Id));
        Assert.Equal(new[] { "z", "a" }, plan.BlockedItems.Select(i => i.Id));
    }

    [Fact]
    public void Result_PreservesDeterministicDeletedAndFailureOrder()
    {
        var deleted = new[] { Item("z", 1, 1), Item("a", 1, 1) };
        var failures = new[] { Failure("z"), Failure("a") };

        var result = new ExtractionCleanupResult(Plan(), Applied: true, deleted, failures);

        Assert.Equal(new[] { "z", "a" }, result.DeletedItems.Select(i => i.Id));
        Assert.Equal(new[] { "z", "a" }, result.Failures.Select(f => f.Id));
    }
}
