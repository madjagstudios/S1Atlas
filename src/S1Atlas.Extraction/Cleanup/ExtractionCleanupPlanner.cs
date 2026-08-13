using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// Reads the database and Atlas-owned filesystem to build a conservative, fail-closed
/// cleanup plan. Planning performs no deletion and no database mutation. A tree is
/// eligible only when both its database facts and its filesystem ownership agree it is
/// safe to remove; anything unknown, changed, active, or ambiguous is blocked.
/// </summary>
internal sealed class ExtractionCleanupPlanner
{
    private static readonly string[] CompletionPoisonFiles =
        ["complete.marker", "artifact-manifest.json"];

    private readonly string _dataRoot;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly TimeProvider _timeProvider;
    private readonly CleanupTreeInspector _treeInspector;
    private readonly ICleanupFileSystem _fileSystem;

    public ExtractionCleanupPlanner(
        string dataRoot,
        IValidatedExtractionRepository validatedRepository,
        TimeProvider timeProvider,
        CleanupTreeInspector treeInspector,
        ICleanupFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _validatedRepository = validatedRepository
            ?? throw new ArgumentNullException(nameof(validatedRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _treeInspector = treeInspector ?? throw new ArgumentNullException(nameof(treeInspector));
        _fileSystem = fileSystem ?? SystemCleanupFileSystem.Instance;
    }

    public async Task<CleanupPlanningResult> PlanAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken)
    {
        if (olderThan <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThan),
                "The cleanup retention window must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = _timeProvider.GetUtcNow() - olderThan;
        var attempts = await _validatedRepository.ListAttemptsAsync(
            buildId: null,
            cancellationToken);
        var attemptsById = new Dictionary<string, ExtractionAttempt>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            attemptsById[attempt.AttemptId] = attempt;
        }

        var candidates = new List<CleanupCandidate>();
        var blocked = new List<ExtractionCleanupBlockedItem>();

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassifyTerminalAttempt(attempt, cutoff, candidates, blocked);
        }

        ScanBuildStagingRoots(attemptsById, cutoff, candidates, blocked, cancellationToken);
        ScanToolStaging(cutoff, candidates, blocked);
        ScanToolQuarantine(cutoff, candidates, blocked);

        candidates.Sort(CompareCandidates);
        blocked.Sort(CompareBlocked);

        var plan = new ExtractionCleanupPlan(
            olderThan,
            cutoff,
            candidates.Select(candidate => candidate.PublicItem).ToArray(),
            blocked);
        return new CleanupPlanningResult(plan, candidates);
    }

    private void ClassifyTerminalAttempt(
        ExtractionAttempt attempt,
        DateTimeOffset cutoff,
        List<CleanupCandidate> candidates,
        List<ExtractionCleanupBlockedItem> blocked)
    {
        if (attempt.Status is not (ExtractionAttemptStatus.Failed
            or ExtractionAttemptStatus.Canceled
            or ExtractionAttemptStatus.Abandoned))
        {
            return;
        }

        if (attempt.CompletedAtUtc is not { } completedAtUtc || completedAtUtc >= cutoff)
        {
            return;
        }

        if (!IsSafeBuildId(attempt.BuildId) ||
            !OwnedAttemptPaths.IsLowerGuidN(attempt.AttemptId))
        {
            blocked.Add(new ExtractionCleanupBlockedItem(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                attempt.AttemptId,
                "CleanupUnsafeIdentity",
                "The attempt build or attempt ID is not a safe owned path segment."));
            return;
        }

        var attemptRoot = Combine("builds", attempt.BuildId, "attempts", attempt.AttemptId);
        var stagingRoot = Combine(
            "builds", attempt.BuildId, "extractions", ".staging", attempt.AttemptId);
        var displayPath = Display(attemptRoot);

        if (attempt.ResultExtractionId is not null)
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                "CleanupHasResultExtraction",
                "The terminal attempt references a validated extraction."));
            return;
        }

        if (attempt.CandidateOutputPath is not null)
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                "CleanupCandidateOutput",
                "The terminal attempt still owns candidate output."));
            return;
        }

        if (EntryExists(stagingRoot + ".promotion.json"))
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                "CleanupPromotionJournal",
                "A promotion journal is still present for the attempt staging."));
            return;
        }

        var attemptObservation = _treeInspector.Inspect(attemptRoot, allowMissing: true);
        if (attemptObservation.Outcome == CleanupObservationOutcome.Blocked)
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                attemptObservation.BlockCode!,
                attemptObservation.BlockMessage!));
            return;
        }

        var stagingObservation = _treeInspector.Inspect(stagingRoot, allowMissing: true);
        if (stagingObservation.Outcome == CleanupObservationOutcome.Blocked)
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                stagingObservation.BlockCode!,
                stagingObservation.BlockMessage!));
            return;
        }

        if (TreeContainsFile(attemptRoot, IsCompletionPoison) ||
            TreeContainsFile(stagingRoot, IsCompletionPoison))
        {
            blocked.Add(Block(
                ExtractionCleanupItemKind.TerminalAttempt,
                attempt.AttemptId,
                displayPath,
                "CleanupCompletionEvidence",
                "The terminal attempt tree contains completion or manifest evidence."));
            return;
        }

        var aggregate = Aggregate(
            [
                ("attempt", attemptObservation.Observation!),
                ("extraction-staging", stagingObservation.Observation!)
            ]);
        var item = new ExtractionCleanupItem(
            ExtractionCleanupItemKind.TerminalAttempt,
            attempt.AttemptId,
            attempt.BuildId,
            attempt.AttemptId,
            displayPath,
            completedAtUtc,
            aggregate.FileCount,
            aggregate.ByteCount);
        candidates.Add(new CleanupCandidate(
            item,
            [attemptRoot, stagingRoot],
            aggregate.Digest,
            attempt.Status,
            completedAtUtc));
    }

    private void ScanBuildStagingRoots(
        IReadOnlyDictionary<string, ExtractionAttempt> attemptsById,
        DateTimeOffset cutoff,
        List<CleanupCandidate> candidates,
        List<ExtractionCleanupBlockedItem> blocked,
        CancellationToken cancellationToken)
    {
        var buildsRoot = Combine("builds");
        foreach (var buildDirectory in EnumerateDirectoryChildren(buildsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buildName = Path.GetFileName(buildDirectory);
            if (!Is64LowerHex(buildName) || !IsExistingDirectory(buildDirectory))
            {
                continue;
            }

            ScanExtractionStaging(
                Path.Combine(buildDirectory, "extractions", ".staging"),
                attemptsById,
                blocked);
            ScanInputStaging(
                Path.Combine(buildDirectory, "inputs", ".staging"),
                cutoff,
                candidates,
                blocked);
        }
    }

    private void ScanExtractionStaging(
        string stagingRoot,
        IReadOnlyDictionary<string, ExtractionAttempt> attemptsById,
        List<ExtractionCleanupBlockedItem> blocked)
    {
        if (!TryEnumerateStagingRoot(
                stagingRoot,
                ExtractionCleanupItemKind.ExtractionStaging,
                blocked,
                out var children))
        {
            return;
        }

        var journalStems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.EndsWith(".promotion.json", StringComparison.Ordinal))
            {
                journalStems.Add(name[..^".promotion.json".Length]);
            }
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.EndsWith(".promotion.json", StringComparison.Ordinal))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ExtractionStaging,
                    name,
                    Display(child),
                    "CleanupPromotionJournal",
                    "A promotion journal is present in extraction staging."));
                continue;
            }

            if (!IsExistingDirectory(child) || !OwnedAttemptPaths.IsLowerGuidN(name))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ExtractionStaging,
                    name,
                    Display(child),
                    "CleanupUnknownEntry",
                    "The extraction staging entry is not an owned attempt directory."));
                continue;
            }

            if (journalStems.Contains(name))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ExtractionStaging,
                    name,
                    Display(child),
                    "CleanupPromotionJournal",
                    "The extraction staging has a sibling promotion journal."));
                continue;
            }

            if (!attemptsById.TryGetValue(name, out var attempt))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ExtractionStaging,
                    name,
                    Display(child),
                    "CleanupOrphanStaging",
                    "The extraction staging has no matching database attempt."));
                continue;
            }

            // A terminal attempt already owns this staging via its candidate; anything
            // else (live, non-terminal, or resumable ProcessCompleted) is preserved.
            if (attempt.Status is not (ExtractionAttemptStatus.Failed
                or ExtractionAttemptStatus.Canceled
                or ExtractionAttemptStatus.Abandoned))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ExtractionStaging,
                    name,
                    Display(child),
                    "CleanupActiveAttempt",
                    $"The extraction staging belongs to a {attempt.Status} attempt."));
            }
        }
    }

    private void ScanInputStaging(
        string stagingRoot,
        DateTimeOffset cutoff,
        List<CleanupCandidate> candidates,
        List<ExtractionCleanupBlockedItem> blocked)
    {
        if (!TryEnumerateStagingRoot(
                stagingRoot,
                ExtractionCleanupItemKind.InputStaging,
                blocked,
                out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (!IsExistingDirectory(child) || !OwnedAttemptPaths.IsLowerGuidN(name))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.InputStaging,
                    name,
                    Display(child),
                    "CleanupUnknownEntry",
                    "The input staging entry is not an owned staging directory."));
                continue;
            }

            var inspection = _treeInspector.Inspect(child, allowMissing: false);
            if (inspection.Outcome == CleanupObservationOutcome.Blocked)
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.InputStaging,
                    name,
                    Display(child),
                    inspection.BlockCode!,
                    inspection.BlockMessage!));
                continue;
            }

            if (inspection.Outcome == CleanupObservationOutcome.Missing)
            {
                continue;
            }

            if (TreeContainsFile(child, fileName =>
                    string.Equals(fileName, "complete.marker", StringComparison.Ordinal)))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.InputStaging,
                    name,
                    Display(child),
                    "CleanupCompletionEvidence",
                    "The input staging still contains a completion marker."));
                continue;
            }

            var observation = inspection.Observation!;
            if (observation.NewestWriteUtc >= cutoff)
            {
                continue;
            }

            AddSinglePathCandidate(
                candidates,
                ExtractionCleanupItemKind.InputStaging,
                name,
                buildId: null,
                child,
                "input-staging",
                observation,
                observation.NewestWriteUtc);
        }
    }

    private void ScanToolStaging(
        DateTimeOffset cutoff,
        List<CleanupCandidate> candidates,
        List<ExtractionCleanupBlockedItem> blocked)
    {
        var stagingRoot = Combine("tools", ".staging");
        if (!TryEnumerateStagingRoot(
                stagingRoot,
                ExtractionCleanupItemKind.ToolStaging,
                blocked,
                out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (!ToolPathPolicy.IsOwnedToolStagingEntryName(name))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ToolStaging,
                    name,
                    Display(child),
                    "CleanupUnknownEntry",
                    "The tool staging entry is not an owned staging directory."));
                continue;
            }

            var inspection = _treeInspector.Inspect(child, allowMissing: false);
            if (!TryEligibleFromInspection(
                    inspection,
                    ExtractionCleanupItemKind.ToolStaging,
                    name,
                    child,
                    blocked,
                    out var observation))
            {
                continue;
            }

            if (observation.NewestWriteUtc >= cutoff)
            {
                continue;
            }

            AddSinglePathCandidate(
                candidates,
                ExtractionCleanupItemKind.ToolStaging,
                name,
                buildId: null,
                child,
                "tool-staging",
                observation,
                observation.NewestWriteUtc);
        }
    }

    private void ScanToolQuarantine(
        DateTimeOffset cutoff,
        List<CleanupCandidate> candidates,
        List<ExtractionCleanupBlockedItem> blocked)
    {
        var quarantineRoot = Combine("tools", "quarantine");
        if (!TryEnumerateStagingRoot(
                quarantineRoot,
                ExtractionCleanupItemKind.ToolQuarantine,
                blocked,
                out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (!ToolPathPolicy.TryGetQuarantineTimestampUtc(name, out var parsedTimestamp))
            {
                blocked.Add(Block(
                    ExtractionCleanupItemKind.ToolQuarantine,
                    name,
                    Display(child),
                    "CleanupUnknownEntry",
                    "The quarantine entry does not match the owned quarantine name."));
                continue;
            }

            var inspection = _treeInspector.Inspect(child, allowMissing: false);
            if (!TryEligibleFromInspection(
                    inspection,
                    ExtractionCleanupItemKind.ToolQuarantine,
                    name,
                    child,
                    blocked,
                    out var observation))
            {
                continue;
            }

            // Age uses the later of the embedded quarantine timestamp and the newest
            // observed write so a recently touched quarantine tree is never removed early.
            var controlling = parsedTimestamp > observation.NewestWriteUtc
                ? parsedTimestamp
                : observation.NewestWriteUtc;
            if (controlling >= cutoff)
            {
                continue;
            }

            AddSinglePathCandidate(
                candidates,
                ExtractionCleanupItemKind.ToolQuarantine,
                name,
                buildId: null,
                child,
                "tool-quarantine",
                observation,
                controlling);
        }
    }

    private bool TryEligibleFromInspection(
        CleanupTreeInspection inspection,
        ExtractionCleanupItemKind kind,
        string id,
        string path,
        List<ExtractionCleanupBlockedItem> blocked,
        out CleanupTreeObservation observation)
    {
        observation = null!;
        if (inspection.Outcome == CleanupObservationOutcome.Blocked)
        {
            blocked.Add(Block(kind, id, Display(path), inspection.BlockCode!, inspection.BlockMessage!));
            return false;
        }

        if (inspection.Outcome == CleanupObservationOutcome.Missing)
        {
            return false;
        }

        observation = inspection.Observation!;
        return true;
    }

    private void AddSinglePathCandidate(
        List<CleanupCandidate> candidates,
        ExtractionCleanupItemKind kind,
        string id,
        string? buildId,
        string ownedPath,
        string role,
        CleanupTreeObservation observation,
        DateTimeOffset controllingTimestamp)
    {
        var aggregate = Aggregate([(role, observation)]);
        var item = new ExtractionCleanupItem(
            kind,
            id,
            buildId,
            AttemptId: null,
            Display(ownedPath),
            controllingTimestamp,
            aggregate.FileCount,
            aggregate.ByteCount);
        candidates.Add(new CleanupCandidate(
            item,
            [ownedPath],
            aggregate.Digest,
            ExpectedAttemptStatus: null,
            ExpectedCompletedAtUtc: null));
    }

    private bool TryEnumerateStagingRoot(
        string root,
        ExtractionCleanupItemKind kind,
        List<ExtractionCleanupBlockedItem> blocked,
        out IReadOnlyList<string> children)
    {
        children = [];
        FileAttributes attributes;
        try
        {
            attributes = _fileSystem.GetAttributes(root);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            blocked.Add(Block(
                kind,
                Path.GetFileName(root),
                Display(root),
                "CleanupUnreadableEntry",
                $"The cleanup scan root could not be inspected: {exception.Message}"));
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            blocked.Add(Block(
                kind,
                Path.GetFileName(root),
                Display(root),
                "CleanupReparsePoint",
                $"The cleanup scan root '{root}' is a reparse point."));
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            return false;
        }

        try
        {
            children = _fileSystem.EnumerateEntries(root).ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            blocked.Add(Block(
                kind,
                Path.GetFileName(root),
                Display(root),
                "CleanupUnreadableEntry",
                $"The cleanup scan root could not be enumerated: {exception.Message}"));
            return false;
        }
    }

    private IReadOnlyList<string> EnumerateDirectoryChildren(string root)
    {
        try
        {
            _ = _fileSystem.GetAttributes(root);
            return _fileSystem.EnumerateEntries(root).ToArray();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException
                or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool TreeContainsFile(string root, Func<string, bool> nameMatches)
    {
        // The tree has already passed a clean inspection (no reparse, readable), so this
        // second guarded walk only classifies entries; it never follows a reparse point.
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = _fileSystem.GetAttributes(root);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return nameMatches(Path.GetFileName(root));
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in _fileSystem.EnumerateEntries(directory))
            {
                var attributes = _fileSystem.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                }
                else if (nameMatches(Path.GetFileName(child)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCompletionPoison(string fileName) =>
        CompletionPoisonFiles.Contains(fileName, StringComparer.Ordinal);

    private bool EntryExists(string path)
    {
        try
        {
            _ = _fileSystem.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private bool IsExistingDirectory(string path)
    {
        try
        {
            return (_fileSystem.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private string Combine(params string[] segments) =>
        Path.Combine([_dataRoot, .. segments]);

    private string Display(string path) =>
        Path.GetRelativePath(_dataRoot, path).Replace('\\', '/');

    private static (int FileCount, long ByteCount, string Digest) Aggregate(
        IReadOnlyList<(string Role, CleanupTreeObservation Observation)> observations)
    {
        var ordered = observations
            .OrderBy(entry => entry.Role, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        var fileCount = 0;
        var byteCount = 0L;
        foreach (var (role, observation) in ordered)
        {
            builder.Append(role);
            builder.Append('\n');
            builder.Append(observation.ObservationDigest);
            builder.Append('\n');
            fileCount = checked(fileCount + observation.FileCount);
            byteCount = checked(byteCount + observation.ByteCount);
        }

        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return (fileCount, byteCount, digest);
    }

    private static ExtractionCleanupBlockedItem Block(
        ExtractionCleanupItemKind kind,
        string id,
        string displayPath,
        string code,
        string message) =>
        new(kind, id, displayPath, code, message);

    private static bool IsSafeBuildId(string buildId) =>
        !string.IsNullOrEmpty(buildId) &&
        Path.GetFileName(buildId) == buildId &&
        buildId is not ("." or "..") &&
        !buildId.Contains(':') &&
        !buildId.Any(char.IsControl);

    private static bool Is64LowerHex(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int CompareCandidates(CleanupCandidate first, CleanupCandidate second)
    {
        var byKind = first.PublicItem.Kind.CompareTo(second.PublicItem.Kind);
        return byKind != 0
            ? byKind
            : string.CompareOrdinal(first.PublicItem.Id, second.PublicItem.Id);
    }

    private static int CompareBlocked(
        ExtractionCleanupBlockedItem first,
        ExtractionCleanupBlockedItem second)
    {
        var byKind = first.Kind.CompareTo(second.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(first.Id, second.Id);
    }
}
