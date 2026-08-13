using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Cleanup;
using Xunit;

namespace S1Atlas.Extraction.Tests.Cleanup;

public sealed class ExtractionCleanupPlannerTests
{
    private const string DataRoot = "C:\\atlas";
    private static readonly string BuildId = new('a', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now - TimeSpan.FromDays(30);
    private static readonly DateTimeOffset Old = Now - TimeSpan.FromDays(40);
    private static readonly DateTimeOffset Recent = Now - TimeSpan.FromDays(10);

    private static string AttemptId(int index) => index.ToString("x32");

    [Theory]
    [InlineData(ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Canceled)]
    [InlineData(ExtractionAttemptStatus.Abandoned)]
    public async Task Plan_TerminalAttemptCompletedBeforeCutoff_IsEligible(
        ExtractionAttemptStatus status)
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(AttemptRoot(id), Old);
        fileSystem.File(AttemptRoot(id) + "\\logs\\stdout.log", 10, Old);
        var planner = CreatePlanner(fileSystem, Attempt(id, status, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var item = Assert.Single(result.PublicPlan.EligibleItems);
        Assert.Equal(ExtractionCleanupItemKind.TerminalAttempt, item.Kind);
        Assert.Equal(id, item.Id);
        Assert.Equal(Old, item.ControllingTimestampUtc);
        Assert.Empty(result.PublicPlan.BlockedItems);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(status, candidate.ExpectedAttemptStatus);
        Assert.Equal(Old, candidate.ExpectedCompletedAtUtc);
        Assert.Equal(2, candidate.OwnedPaths.Count);
    }

    [Fact]
    public async Task Plan_TerminalAttemptCompletedExactlyAtCutoff_IsNotEligible()
    {
        var id = AttemptId(1);
        var planner = CreatePlanner(
            new FakeCleanupFileSystem(),
            Attempt(id, ExtractionAttemptStatus.Failed, Cutoff));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
        Assert.Empty(result.PublicPlan.BlockedItems);
    }

    [Fact]
    public async Task Plan_TerminalAttemptCompletedAfterCutoff_IsNotEligible()
    {
        var id = AttemptId(1);
        var planner = CreatePlanner(
            new FakeCleanupFileSystem(),
            Attempt(id, ExtractionAttemptStatus.Failed, Recent));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.ProcessCompleted)]
    [InlineData(ExtractionAttemptStatus.Succeeded)]
    public async Task Plan_NonTerminalOrSucceededAttempt_IsNeverEligible(
        ExtractionAttemptStatus status)
    {
        var id = AttemptId(1);
        var planner = CreatePlanner(new FakeCleanupFileSystem(), Attempt(id, status, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
        Assert.Empty(result.PublicPlan.BlockedItems);
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithResultExtraction_IsBlocked()
    {
        var id = AttemptId(1);
        var planner = CreatePlanner(
            new FakeCleanupFileSystem(),
            Attempt(id, ExtractionAttemptStatus.Failed, Old, resultExtractionId: "extraction-1"));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupHasResultExtraction", blocked.Code);
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithCandidateOutput_IsBlocked()
    {
        var id = AttemptId(1);
        var planner = CreatePlanner(
            new FakeCleanupFileSystem(),
            Attempt(
                id,
                ExtractionAttemptStatus.Failed,
                Old,
                candidateOutputPath: "C:\\atlas\\builds\\x\\attempts\\y\\candidate-output"));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupCandidateOutput", blocked.Code);
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithCompletionEvidence_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(AttemptRoot(id), Old);
        fileSystem.File(AttemptRoot(id) + "\\complete.marker", 5, Old);
        var planner = CreatePlanner(fileSystem, Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupCompletionEvidence", blocked.Code);
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithPromotionJournal_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(AttemptRoot(id), Old);
        fileSystem.File(StagingRoot(id) + ".promotion.json", 5, Old);
        var planner = CreatePlanner(fileSystem, Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        // A promotion journal is ambiguous evidence: it blocks both the terminal attempt
        // and the staging scan, and nothing is eligible.
        Assert.Empty(result.PublicPlan.EligibleItems);
        Assert.Contains(
            result.PublicPlan.BlockedItems,
            blocked => blocked.Kind == ExtractionCleanupItemKind.TerminalAttempt &&
                blocked.Code == "CleanupPromotionJournal");
        Assert.All(
            result.PublicPlan.BlockedItems,
            blocked => Assert.Equal("CleanupPromotionJournal", blocked.Code));
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithReparsePoint_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(AttemptRoot(id), Old, reparsePoint: true);
        var planner = CreatePlanner(fileSystem, Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupReparsePoint", blocked.Code);
    }

    [Fact]
    public async Task Plan_TerminalAttemptWithMissingRoot_IsEligibleWithZeroBytes()
    {
        var id = AttemptId(1);
        // No filesystem entries at all: the DB row can still converge to deleted.
        var planner = CreatePlanner(
            new FakeCleanupFileSystem(),
            Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var item = Assert.Single(result.PublicPlan.EligibleItems);
        Assert.Equal(0, item.FileCount);
        Assert.Equal(0, item.ByteCount);
    }

    [Fact]
    public async Task Plan_RecognizedInputStagingOlderThanCutoff_IsEligible()
    {
        var staging = AttemptId(7);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(InputStagingRoot(staging), Old);
        fileSystem.File(InputStagingRoot(staging) + "\\game-root\\a.bin", 12, Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var item = Assert.Single(result.PublicPlan.EligibleItems);
        Assert.Equal(ExtractionCleanupItemKind.InputStaging, item.Kind);
        Assert.Equal(staging, item.Id);
    }

    [Fact]
    public async Task Plan_NewestWriteAnywhereInTree_ControlsAge()
    {
        var staging = AttemptId(7);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(InputStagingRoot(staging), Old);
        // A recent nested write keeps the whole tree ineligible.
        fileSystem.File(InputStagingRoot(staging) + "\\game-root\\fresh.bin", 12, Recent);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
    }

    [Fact]
    public async Task Plan_UnknownStagingChildName_IsBlocked()
    {
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(ToolStagingRoot() + "\\not-owned", Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal(ExtractionCleanupItemKind.ToolStaging, blocked.Kind);
        Assert.Equal("CleanupUnknownEntry", blocked.Code);
    }

    [Fact]
    public async Task Plan_ExtractionStagingWithSiblingPromotionJournal_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(StagingRoot(id), Old);
        fileSystem.File(StagingRoot(id) + ".promotion.json", 5, Old);
        var planner = CreatePlanner(
            fileSystem,
            Attempt(id, ExtractionAttemptStatus.Failed, Recent));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Contains(
            result.PublicPlan.BlockedItems,
            blocked => blocked.Kind == ExtractionCleanupItemKind.ExtractionStaging &&
                blocked.Code == "CleanupPromotionJournal");
    }

    [Fact]
    public async Task Plan_ExtractionStagingWithoutMatchingAttempt_IsBlockedAsOrphan()
    {
        var orphan = AttemptId(9);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(StagingRoot(orphan), Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupOrphanStaging", blocked.Code);
    }

    [Fact]
    public async Task Plan_ExtractionStagingForActiveAttempt_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(StagingRoot(id), Old);
        var planner = CreatePlanner(
            fileSystem,
            Attempt(id, ExtractionAttemptStatus.Running, completedAtUtc: null));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupActiveAttempt", blocked.Code);
    }

    [Fact]
    public async Task Plan_ExtractionStagingForProcessCompletedAttempt_IsBlocked()
    {
        var id = AttemptId(1);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(StagingRoot(id), Old);
        var planner = CreatePlanner(
            fileSystem,
            Attempt(id, ExtractionAttemptStatus.ProcessCompleted, completedAtUtc: null));

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupActiveAttempt", blocked.Code);
    }

    [Fact]
    public async Task Plan_InputStagingContainingCompleteMarker_IsBlocked()
    {
        var staging = AttemptId(7);
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(InputStagingRoot(staging), Old);
        fileSystem.File(InputStagingRoot(staging) + "\\complete.marker", 5, Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupCompletionEvidence", blocked.Code);
    }

    [Fact]
    public async Task Plan_NeverEnumeratesFinalSnapshotExtractionQuarantineOrToolRoots()
    {
        var fileSystem = new FakeCleanupFileSystem();
        // Input snapshot final directory (not under .staging).
        fileSystem.File(
            $"{DataRoot}\\builds\\{BuildId}\\inputs\\{new string('b', 64)}\\complete.marker", 1, Old);
        // Validated extraction final directory + its Phase 4 quarantine.
        fileSystem.File(
            $"{DataRoot}\\builds\\{BuildId}\\extractions\\{new string('c', 64)}\\complete.marker", 1, Old);
        fileSystem.File(
            $"{DataRoot}\\builds\\{BuildId}\\extractions\\quarantine\\old\\a.bin", 1, Old);
        // Current managed-tool installation root.
        fileSystem.File($"{DataRoot}\\tools\\cpp2il\\1.0\\Cpp2IL.exe", 1, Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
        Assert.Empty(result.PublicPlan.BlockedItems);
    }

    [Fact]
    public async Task Plan_ToolQuarantineAge_UsesLaterOfParsedTimestampAndNewestWrite()
    {
        // Parsed timestamp is recent even though the file write time is old.
        var recentStamp = Recent.ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"cpp2il-1.0-{recentStamp}-{new string('0', 32)}";
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.File($"{ToolQuarantineRoot()}\\{name}", 4, Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.PublicPlan.EligibleItems);
        Assert.Empty(result.PublicPlan.BlockedItems);
    }

    [Fact]
    public async Task Plan_OldToolQuarantineEntry_IsEligible()
    {
        var oldStamp = Old.ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"cpp2il-1.0-{oldStamp}-{new string('0', 32)}";
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.File($"{ToolQuarantineRoot()}\\{name}", 4, Old);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var item = Assert.Single(result.PublicPlan.EligibleItems);
        Assert.Equal(ExtractionCleanupItemKind.ToolQuarantine, item.Kind);
    }

    [Fact]
    public async Task Plan_ReparsePointStagingRoot_IsBlocked()
    {
        var fileSystem = new FakeCleanupFileSystem();
        fileSystem.Directory(ToolStagingRoot(), Old, reparsePoint: true);
        var planner = CreatePlanner(fileSystem);

        var result = await planner.PlanAsync(TimeSpan.FromDays(30), Ct);

        var blocked = Assert.Single(result.PublicPlan.BlockedItems);
        Assert.Equal("CleanupReparsePoint", blocked.Code);
    }

    [Fact]
    public async Task Plan_NonPositiveOlderThan_Throws()
    {
        var planner = CreatePlanner(new FakeCleanupFileSystem());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            planner.PlanAsync(TimeSpan.Zero, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AttemptRoot(string id) =>
        $"{DataRoot}\\builds\\{BuildId}\\attempts\\{id}";

    private static string StagingRoot(string id) =>
        $"{DataRoot}\\builds\\{BuildId}\\extractions\\.staging\\{id}";

    private static string InputStagingRoot(string id) =>
        $"{DataRoot}\\builds\\{BuildId}\\inputs\\.staging\\{id}";

    private static string ToolStagingRoot() => $"{DataRoot}\\tools\\.staging";

    private static string ToolQuarantineRoot() => $"{DataRoot}\\tools\\quarantine";

    private static ExtractionCleanupPlanner CreatePlanner(
        FakeCleanupFileSystem fileSystem,
        params ExtractionAttempt[] attempts) =>
        new(
            DataRoot,
            new FakeAttemptRepository(attempts),
            new FixedTimeProvider(Now),
            new CleanupTreeInspector(fileSystem),
            fileSystem);

    private static ExtractionAttempt Attempt(
        string attemptId,
        ExtractionAttemptStatus status,
        DateTimeOffset? completedAtUtc,
        string? resultExtractionId = null,
        string? candidateOutputPath = null) =>
        new(
            AttemptId: attemptId,
            RecipeId: "recipe-1",
            BuildId: BuildId,
            ToolInstanceId: null,
            ProfileId: "default",
            ProfileVersion: 1,
            ProfileDigest: new string('a', 64),
            ValidationPolicyId: "default",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: new string('b', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: null,
            InputSnapshotId: null,
            Status: status,
            CreatedAtUtc: Old,
            StartedAtUtc: null,
            CompletedAtUtc: completedAtUtc,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: "C:\\attempts\\work",
            StandardOutputPath: "C:\\attempts\\logs\\stdout.log",
            StandardErrorPath: "C:\\attempts\\logs\\stderr.log",
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: null,
            ProcessExitCode: null,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            KeepFailedArtifacts: false,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: candidateOutputPath,
            ResultExtractionId: resultExtractionId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAttemptRepository(ExtractionAttempt[] attempts)
        : IValidatedExtractionRepository
    {
        public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
            string? buildId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractionAttempt>>(attempts);

        public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
            string extractionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
            string extractionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
            string? buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredValidationResult?> GetLatestValidationResultAsync(
            string extractionId, string policyDigest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PreferredExtraction?> GetPreferredExtractionAsync(
            string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveValidationFailureAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitValidatedExtractionAsync(
            ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LinkAttemptToValidatedExtractionAsync(
            ValidationPersistence validation, ValidatedExtraction extraction,
            ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveRevalidationAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetPreferredExtractionAsync(
            PreferredExtraction preference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearPreferredExtractionAsync(
            string buildId, string expectedExtractionId, ExtractionPreferenceReason reason,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteCleanupEligibleAttemptAsync(
            string attemptId, ExtractionAttemptStatus expectedStatus,
            DateTimeOffset expectedCompletedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCleanupFileSystem : ICleanupFileSystem
    {
        private static readonly DateTimeOffset DefaultWrite =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

        public FakeCleanupFileSystem()
        {
            _nodes[DataRoot] = new Node(FileAttributes.Directory, 0, DefaultWrite, false);
            _children[DataRoot] = [];
        }

        public void Directory(
            string path,
            DateTimeOffset lastWrite,
            bool reparsePoint = false)
        {
            EnsureAncestors(path);
            var attributes = FileAttributes.Directory;
            if (reparsePoint)
            {
                attributes |= FileAttributes.ReparsePoint;
            }

            _nodes[path] = new Node(attributes, 0, lastWrite, false);
            _children.TryAdd(path, []);
        }

        public void File(
            string path,
            long length,
            DateTimeOffset lastWrite,
            bool reparsePoint = false)
        {
            EnsureAncestors(path);
            var attributes = FileAttributes.Normal;
            if (reparsePoint)
            {
                attributes |= FileAttributes.ReparsePoint;
            }

            _nodes[path] = new Node(attributes, length, lastWrite, false);
        }

        public FileAttributes GetAttributes(string path)
        {
            if (!_nodes.TryGetValue(path, out var node))
            {
                throw new FileNotFoundException(path);
            }

            return node.Attributes;
        }

        public IEnumerable<string> EnumerateEntries(string path) =>
            _children.TryGetValue(path, out var list) ? list.ToArray() : [];

        public long GetFileLength(string path) => _nodes[path].Length;

        public DateTimeOffset GetLastWriteUtc(string path) => _nodes[path].LastWriteUtc;

        private void EnsureAncestors(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null)
            {
                return;
            }

            if (!_nodes.ContainsKey(parent))
            {
                EnsureAncestors(parent);
                _nodes[parent] = new Node(FileAttributes.Directory, 0, DefaultWrite, false);
                _children[parent] = [];
                AddToParent(parent);
            }

            AddToParent(path);
        }

        private void AddToParent(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null)
            {
                return;
            }

            _children.TryAdd(parent, []);
            if (!_children[parent].Contains(path))
            {
                _children[parent].Add(path);
            }
        }

        private sealed record Node(
            FileAttributes Attributes,
            long Length,
            DateTimeOffset LastWriteUtc,
            bool Unreadable);
    }
}
