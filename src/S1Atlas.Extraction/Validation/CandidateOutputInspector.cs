using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Promotion;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// Deterministically inventories the contained bytes of a Phase 3
/// <c>ProcessCompleted</c> candidate output tree without classifying managed
/// metadata. Never follows reparse points, hashes every regular file exactly
/// once, re-observes each file to reject a race, rejects ordinal-ignore-case
/// duplicate relative paths, and uses checked arithmetic for totals.
/// </summary>
/// <remarks>
/// Containment, race, unreadable, and path-collision defects return a
/// <see cref="CandidateInspectionResult"/> with <c>Inventory = null</c>,
/// <c>ContainmentPassed = false</c>, and a single <see cref="ValidationIssue"/>
/// describing the defect, rather than throwing a generic exception, so a caller
/// can still write a strict validation.json and fail the attempt. Cancellation
/// and inability to prove Atlas ownership safely (an unsafe attempt/build ID, or
/// a reparse point in the owned attempt ancestry) may still throw.
/// </remarks>
internal sealed class CandidateOutputInspector
{
    private const string ReconstructedPrefix = "reconstructed/";
    private const string ContainmentIssueCode = "OutputOutsideStaging";

    private readonly IFileHasher _fileHasher;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, string[]> _getFiles;
    private readonly Func<string, string[]> _getDirectories;
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<string, CandidateFileObservation?> _observeFile;

    public CandidateOutputInspector(IFileHasher fileHasher)
        : this(
            fileHasher,
            Directory.Exists,
            directory => Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly),
            directory => Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly),
            File.GetAttributes,
            DefaultObserveFile)
    {
    }

    internal CandidateOutputInspector(
        IFileHasher fileHasher,
        Func<string, bool> directoryExists,
        Func<string, string[]> getFiles,
        Func<string, string[]> getDirectories,
        Func<string, FileAttributes> getAttributes,
        Func<string, CandidateFileObservation?> observeFile)
    {
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _getFiles = getFiles ?? throw new ArgumentNullException(nameof(getFiles));
        _getDirectories = getDirectories ?? throw new ArgumentNullException(nameof(getDirectories));
        _getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
        _observeFile = observeFile ?? throw new ArgumentNullException(nameof(observeFile));
    }

    public async Task<CandidateInspectionResult> InspectAsync(
        ExtractionAttempt attempt,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (attempt.Status is not (ExtractionAttemptStatus.ProcessCompleted or
            ExtractionAttemptStatus.Validating))
        {
            return Invalid(
                $"Attempt status '{attempt.Status}' cannot be inventoried for containment.");
        }

        if (string.IsNullOrWhiteSpace(attempt.CandidateOutputPath))
        {
            return Invalid("The attempt has no candidate output path to inventory.");
        }

        OwnedValidatedExtractionPaths ownedPaths;
        try
        {
            ownedPaths = OwnedValidatedExtractionPaths.ForAttempt(
                dataRoot, attempt.BuildId, attempt.AttemptId);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid($"The attempt's owned candidate path is unsafe: {exception.Message}");
        }

        string ownedCandidateRoot;
        string actualCandidateRoot;
        try
        {
            ownedCandidateRoot = Path.GetFullPath(ownedPaths.AttemptCandidatePath!);
            actualCandidateRoot = Path.GetFullPath(attempt.CandidateOutputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid("The candidate output path could not be resolved.");
        }

        if (!string.Equals(actualCandidateRoot, ownedCandidateRoot, StringComparison.Ordinal))
        {
            return Invalid(
                "The attempt's candidate output path does not equal its owned candidate path.");
        }

        try
        {
            OwnedAttemptPaths.EnsureSafeExistingPath(ownedPaths.DataRoot, ownedCandidateRoot);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid($"The candidate output root is unsafe: {exception.Message}");
        }

        if (!_directoryExists(ownedCandidateRoot))
        {
            return Invalid("The candidate output root does not exist.");
        }

        var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<CandidateArtifact>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(ownedCandidateRoot);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            string[] files;
            string[] directories;
            try
            {
                files = _getFiles(currentDirectory) ?? [];
                directories = _getDirectories(currentDirectory) ?? [];
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Invalid($"Candidate directory '{currentDirectory}' could not be read.");
            }

            foreach (var file in files.OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await InspectFileAsync(
                    ownedCandidateRoot, file, seenRelativePaths, cancellationToken);
                if (result.Issue is not null)
                {
                    return new CandidateInspectionResult(
                        Inventory: null,
                        ContainmentPassed: false,
                        Issues: [result.Issue]);
                }

                artifacts.Add(result.Artifact!);
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = _getAttributes(directory);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    return Invalid($"Candidate directory '{directory}' could not be inspected.");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return Invalid($"Candidate directory '{directory}' is a reparse point.");
                }

                pendingDirectories.Push(directory);
            }
        }

        var sortedArtifacts = artifacts
            .OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal)
            .ToArray();

        long totalBytes;
        try
        {
            checked
            {
                totalBytes = 0;
                foreach (var artifact in sortedArtifacts)
                {
                    totalBytes += artifact.Size;
                }
            }
        }
        catch (OverflowException)
        {
            return Invalid("The candidate output byte total overflowed.");
        }

        var inventory = new CandidateInventory(ownedCandidateRoot, sortedArtifacts, totalBytes);
        return new CandidateInspectionResult(inventory, ContainmentPassed: true, Issues: []);
    }

    private async Task<(CandidateArtifact? Artifact, ValidationIssue? Issue)> InspectFileAsync(
        string candidateRoot,
        string file,
        HashSet<string> seenRelativePaths,
        CancellationToken cancellationToken)
    {
        if (!TryComputeRelativePath(candidateRoot, file, out var relativePath))
        {
            return (null, IssueFor($"Candidate file '{file}' escapes its owned candidate root."));
        }

        FileAttributes attributes;
        try
        {
            attributes = _getAttributes(file);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return (null, IssueFor($"Candidate file '{file}' could not be inspected."));
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return (null, IssueFor($"Candidate file '{file}' is a reparse point."));
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return (null, IssueFor($"Candidate file '{file}' is not a regular file."));
        }

        var finalRelativePath = ReconstructedPrefix + relativePath;
        if (!seenRelativePaths.Add(finalRelativePath))
        {
            return (null, IssueFor(
                $"Candidate output contains an ambiguous duplicate path '{finalRelativePath}'."));
        }

        var before = _observeFile(file);
        if (before is null)
        {
            return (null, IssueFor($"Candidate file '{file}' is not a readable regular file."));
        }

        string hash;
        try
        {
            hash = await _fileHasher.ComputeSha256Async(file, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return (null, IssueFor($"Candidate file '{file}' could not be hashed."));
        }

        var after = _observeFile(file);
        if (after is null || !before.Value.Equals(after.Value))
        {
            return (null, IssueFor($"Candidate file '{file}' changed while it was being inspected."));
        }

        return (new CandidateArtifact(file, finalRelativePath, after.Value.Length, hash), null);
    }

    private static bool TryComputeRelativePath(string root, string fullPath, out string relativePath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        relativePath = relative;
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith("../", StringComparison.Ordinal);
    }

    private static CandidateInspectionResult Invalid(string message) =>
        new(Inventory: null, ContainmentPassed: false, Issues: [IssueFor(message)]);

    private static ValidationIssue IssueFor(string message) =>
        new(
            ValidationIssueSeverity.Error,
            ContainmentIssueCode,
            message,
            ArtifactRelativePath: null,
            PreferenceBlocking: true);

    private static CandidateFileObservation? DefaultObserveFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return null;
            }

            var info = new FileInfo(path);
            return new CandidateFileObservation(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal readonly record struct CandidateFileObservation(long Length, DateTimeOffset LastWriteUtc);
