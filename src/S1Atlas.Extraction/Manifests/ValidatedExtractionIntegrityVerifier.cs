using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Promotion;

namespace S1Atlas.Extraction.Manifests;

/// <summary>
/// Proves full integrity of a validated extraction: that its immutable documents,
/// <c>complete.marker</c> binding, database extraction/artifact rows, and current
/// on-disk artifact bytes all agree, and that no artifact or log path is a reparse
/// point. This is the single gate a caller must pass before treating a validated
/// extraction as authoritative (Global Constraint: "Only a validated extraction
/// whose database row, complete.marker, immutable manifests, database artifact
/// rows, and current artifact hashes agree may be returned as authoritative").
/// Never mutates preference; it only reports a verdict.
/// </summary>
public sealed class ValidatedExtractionIntegrityVerifier : IValidatedExtractionIntegrityVerifier
{
    private const string ReconstructedDirectoryName = "reconstructed";
    private const string LogsDirectoryName = "logs";

    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly IFileHasher _fileHasher;
    private readonly IValidatedExtractionRepository? _repository;

    internal ValidatedExtractionIntegrityVerifier(
        ValidatedExtractionDocumentStore documentStore,
        IFileHasher fileHasher,
        IValidatedExtractionRepository? repository = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        _repository = repository;
    }

    /// <summary>
    /// Verifies the extraction identified by <paramref name="extractionId"/> against
    /// its repository row and artifact rows, at its owned final extraction root.
    /// Requires a repository to have been supplied to the constructor.
    /// </summary>
    public async Task<ValidatedExtractionIntegrity> VerifyAsync(
        string dataRoot,
        string buildId,
        string extractionId,
        CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            throw new InvalidOperationException(
                "No repository was supplied; use the explicit-facts VerifyAsync overload instead.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var extraction = await _repository.GetValidatedExtractionAsync(extractionId, cancellationToken);
        if (extraction is null)
        {
            return Missing(
                "ValidatedExtractionRowMissing",
                $"No validated extraction row exists for '{extractionId}'.");
        }

        var artifacts = await _repository.GetExtractionArtifactsAsync(extractionId, cancellationToken);

        string documentsRoot;
        try
        {
            documentsRoot = OwnedValidatedExtractionPaths
                .ForExtraction(dataRoot, buildId, extractionId).FinalExtractionRoot!;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return Missing("ExtractionRootUnsafe", exception.Message);
        }

        return await VerifyAsync(dataRoot, documentsRoot, extraction, artifacts, cancellationToken);
    }

    /// <summary>
    /// Verifies explicitly supplied expected facts (a database row, or a planned
    /// record awaiting commit) against the documents and artifact bytes rooted at
    /// <paramref name="documentsRoot"/>.
    /// </summary>
    public async Task<ValidatedExtractionIntegrity> VerifyAsync(
        string dataRoot,
        string documentsRoot,
        ValidatedExtraction expectedExtraction,
        IReadOnlyList<ArtifactManifestEntry> expectedArtifacts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsRoot);
        ArgumentNullException.ThrowIfNull(expectedExtraction);
        ArgumentNullException.ThrowIfNull(expectedArtifacts);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: validate expected root.
        string root;
        string target;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
            target = Path.GetFullPath(documentsRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Missing("ExtractionRootUnsafe", "The extraction root could not be resolved.");
        }

        if (!OwnedAttemptPaths.IsSameOrDescendant(root, target) ||
            string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            return Missing("ExtractionRootUnsafe", "The extraction root escapes its Atlas data root.");
        }

        // Step 2: require a normal (non-reparse) root, strict docs, and the marker.
        if (!Directory.Exists(target))
        {
            return Missing("ExtractionRootMissing", "The extraction root does not exist.");
        }

        if (!PathSafety.IsNormalDirectory(target))
        {
            return Missing(
                "ExtractionRootUnsafe", "The extraction root is a reparse point or otherwise unsafe.");
        }

        var bundle = await _documentStore.TryReadFinalDocumentsAsync(target, cancellationToken);
        if (bundle is null)
        {
            return Incomplete(
                "DocumentsIncomplete",
                "One or more of extraction.json, artifact-manifest.json, validation.json, or " +
                "complete.marker is missing or does not match its strict schema.");
        }

        // Step 3: hash the three bound documents and compare against the marker.
        var extractionSha256 = await _documentStore.TryComputeDocumentSha256Async(
            bundle.Paths.ExtractionJsonPath, cancellationToken);
        var artifactManifestSha256 = await _documentStore.TryComputeDocumentSha256Async(
            bundle.Paths.ArtifactManifestJsonPath, cancellationToken);
        var validationSha256 = await _documentStore.TryComputeDocumentSha256Async(
            bundle.Paths.ValidationJsonPath, cancellationToken);
        if (extractionSha256 is null || artifactManifestSha256 is null || validationSha256 is null)
        {
            return Incomplete(
                "DocumentsIncomplete", "A bound document could not be re-read to verify the marker.");
        }

        if (!string.Equals(extractionSha256, bundle.CompleteMarker.ExtractionDocumentSha256, StringComparison.Ordinal) ||
            !string.Equals(artifactManifestSha256, bundle.CompleteMarker.ArtifactManifestDocumentSha256, StringComparison.Ordinal) ||
            !string.Equals(validationSha256, bundle.CompleteMarker.ValidationDocumentSha256, StringComparison.Ordinal))
        {
            return Mismatch(
                "MarkerHashMismatch",
                "complete.marker does not bind the current bytes of extraction.json, " +
                "artifact-manifest.json, and validation.json.");
        }

        // Step 4: recompute the artifact manifest digest and extraction ID.
        string recomputedDigest;
        string recomputedExtractionId;
        try
        {
            recomputedDigest = ArtifactManifestFingerprint.Create(bundle.ArtifactManifest.Manifest);
            recomputedExtractionId = ExtractionId.Create(bundle.Extraction.RecipeId, recomputedDigest);
        }
        catch (ArgumentException exception)
        {
            return Mismatch("RecomputationFailed", exception.Message);
        }

        if (!string.Equals(recomputedDigest, bundle.ArtifactManifest.DeclaredDigest, StringComparison.Ordinal) ||
            !string.Equals(recomputedDigest, bundle.Extraction.ArtifactManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(recomputedDigest, bundle.CompleteMarker.ArtifactManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(recomputedDigest, expectedExtraction.ArtifactManifestDigest, StringComparison.Ordinal))
        {
            return Mismatch(
                "ArtifactManifestDigestMismatch",
                "The recomputed artifact manifest digest does not agree with the documents or database row.");
        }

        if (!string.Equals(recomputedExtractionId, bundle.Extraction.ExtractionId, StringComparison.Ordinal) ||
            !string.Equals(recomputedExtractionId, bundle.CompleteMarker.ExtractionId, StringComparison.Ordinal) ||
            !string.Equals(recomputedExtractionId, expectedExtraction.ExtractionId, StringComparison.Ordinal))
        {
            return Mismatch(
                "ExtractionIdMismatch",
                "The recomputed extraction ID does not agree with the documents or database row.");
        }

        // Step 5: compare the extraction document with the database ValidatedExtraction row.
        if (!ExtractionMatchesExpected(bundle.Extraction, expectedExtraction))
        {
            return Mismatch(
                "ExtractionRecordMismatch",
                "extraction.json does not agree with the database validated extraction row.");
        }

        // Step 6: compare every artifact annotation/path/size/hash with the database artifact rows.
        var manifestEntries = bundle.ArtifactManifest.Manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var expectedEntries = expectedArtifacts
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (!manifestEntries.SequenceEqual(expectedEntries))
        {
            return Mismatch(
                "ArtifactRowMismatch",
                "The immutable artifact manifest does not agree with the database artifact rows.");
        }

        // Step 7: enumerate the reconstructed artifact tree without following reparse points.
        var reconstructedRoot = Path.Combine(target, ReconstructedDirectoryName);
        if (!TryEnumerateNormalTree(reconstructedRoot, out var discoveredFiles, out var treeFailure))
        {
            return Mismatch("ReconstructedTreeUnsafe", treeFailure!);
        }

        // Step 8: require an exact path set and re-hash every artifact.
        var manifestPaths = manifestEntries
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var discoveredPaths = discoveredFiles.Keys.ToHashSet(StringComparer.Ordinal);
        if (!manifestPaths.SetEquals(discoveredPaths))
        {
            return Mismatch(
                "ArtifactPathSetMismatch",
                "The reconstructed artifact tree does not contain exactly the manifest's declared paths.");
        }

        foreach (var entry in manifestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = discoveredFiles[entry.RelativePath];

            long actualSize;
            try
            {
                actualSize = new FileInfo(fullPath).Length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Mismatch("ArtifactContentMismatch", $"Artifact '{entry.RelativePath}' could not be inspected.");
            }

            if (actualSize != entry.Size)
            {
                return Mismatch(
                    "ArtifactContentMismatch", $"Artifact '{entry.RelativePath}' size no longer matches the manifest.");
            }

            string actualHash;
            try
            {
                actualHash = await _fileHasher.ComputeSha256Async(fullPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Mismatch("ArtifactContentMismatch", $"Artifact '{entry.RelativePath}' could not be hashed.");
            }

            if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
            {
                return Mismatch(
                    "ArtifactContentMismatch", $"Artifact '{entry.RelativePath}' hash no longer matches the manifest.");
            }
        }

        // Step 9: require document statistics agree with artifact annotations and database aggregates.
        var recomputedStatistics = RecomputeStatistics(manifestEntries);
        if (!StatisticsEqual(recomputedStatistics, bundle.Extraction.Statistics) ||
            !AggregateStatisticsEqual(recomputedStatistics, expectedExtraction.Statistics))
        {
            return Mismatch(
                "StatisticsMismatch",
                "The extraction's declared statistics do not agree with its artifact annotations " +
                "or the database aggregate fields.");
        }

        // Step 10: logs are diagnostics only; require any present logs path to be contained/normal.
        var logsRoot = Path.Combine(target, LogsDirectoryName);
        if (Directory.Exists(logsRoot) &&
            !TryEnumerateNormalTree(logsRoot, out _, out var logsFailure))
        {
            return Mismatch("LogsPathUnsafe", logsFailure!);
        }

        return Valid();
    }

    private static bool ExtractionMatchesExpected(
        ExtractionDocumentContent document, ValidatedExtraction expected) =>
        string.Equals(document.RecipeId, expected.RecipeId, StringComparison.Ordinal) &&
        string.Equals(document.SourceAttemptId, expected.SourceAttemptId, StringComparison.Ordinal) &&
        string.Equals(document.BuildId, expected.BuildId, StringComparison.Ordinal) &&
        string.Equals(document.ToolInstanceId, expected.ToolInstanceId, StringComparison.Ordinal) &&
        string.Equals(document.ProfileId, expected.ProfileId, StringComparison.Ordinal) &&
        document.ProfileVersion == expected.ProfileVersion &&
        string.Equals(document.ProfileDigest, expected.ProfileDigest, StringComparison.Ordinal) &&
        document.AdapterVersion == expected.AdapterVersion &&
        document.ExtractionSchemaVersion == expected.ExtractionSchemaVersion &&
        string.Equals(document.ArtifactManifestDigest, expected.ArtifactManifestDigest, StringComparison.Ordinal) &&
        document.CreatedAtUtc == expected.CreatedAtUtc &&
        document.TrustLevel == expected.TrustLevel &&
        document.InitialValidationOutcome == expected.InitialValidationOutcome &&
        // The validated_extractions row stores only aggregate statistics; the per-assembly
        // breakdown lives in extraction_artifacts and is verified against the immutable
        // artifact manifest below (recomputed vs document). So the database-row comparison
        // is aggregate-only — comparing Assemblies here would always fail because the
        // reconstructed row carries an empty Assemblies list.
        AggregateStatisticsEqual(document.Statistics, expected.Statistics);

    private static bool AggregateStatisticsEqual(ExtractionStatistics a, ExtractionStatistics b) =>
        a.ArtifactCount == b.ArtifactCount &&
        a.LibraryCount == b.LibraryCount &&
        a.ManagedAssemblyCount == b.ManagedAssemblyCount &&
        a.TypeDefinitionCount == b.TypeDefinitionCount &&
        a.MethodDefinitionCount == b.MethodDefinitionCount &&
        a.FieldDefinitionCount == b.FieldDefinitionCount &&
        a.PropertyDefinitionCount == b.PropertyDefinitionCount &&
        a.EventDefinitionCount == b.EventDefinitionCount &&
        a.TotalOutputBytes == b.TotalOutputBytes &&
        a.TotalManagedBytes == b.TotalManagedBytes;

    private static bool StatisticsEqual(ExtractionStatistics a, ExtractionStatistics b) =>
        AggregateStatisticsEqual(a, b) &&
        a.Assemblies.SequenceEqual(b.Assemblies);

    /// <summary>
    /// Independently recomputes aggregate statistics purely from a verified artifact
    /// manifest, mirroring <c>ArtifactManifestBuilder</c>'s grouping rules (assembly
    /// identity grouped ordinal-ignore-case, then ordinal) so a corrupted or
    /// hand-edited <c>statistics</c> block cannot silently diverge from the artifacts
    /// it claims to summarize.
    /// </summary>
    private static ExtractionStatistics RecomputeStatistics(IReadOnlyList<ArtifactManifestEntry> entries)
    {
        var assemblyGroups = entries
            .Where(entry => entry.Kind == ArtifactKind.ManagedAssembly && entry.AssemblyName is not null)
            .GroupBy(entry => entry.AssemblyName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        var assemblies = new List<AssemblyIdentityStatistics>();
        foreach (var group in assemblyGroups)
        {
            var groupEntries = group.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
            assemblies.Add(new AssemblyIdentityStatistics(
                group.Key,
                groupEntries.Length,
                groupEntries.Sum(entry => entry.Size),
                groupEntries.Sum(entry => entry.TypeDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.MethodDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.FieldDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.PropertyDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.EventDefinitionCount ?? 0)));
        }

        return new ExtractionStatistics(
            ArtifactCount: entries.Count,
            LibraryCount: entries.Count(entry =>
                entry.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
            ManagedAssemblyCount: entries.Count(entry => entry.Kind == ArtifactKind.ManagedAssembly),
            TypeDefinitionCount: assemblies.Sum(assembly => assembly.TypeDefinitionCount),
            MethodDefinitionCount: assemblies.Sum(assembly => assembly.MethodDefinitionCount),
            FieldDefinitionCount: assemblies.Sum(assembly => assembly.FieldDefinitionCount),
            PropertyDefinitionCount: assemblies.Sum(assembly => assembly.PropertyDefinitionCount),
            EventDefinitionCount: assemblies.Sum(assembly => assembly.EventDefinitionCount),
            TotalOutputBytes: entries.Sum(entry => entry.Size),
            TotalManagedBytes: entries
                .Where(entry => entry.Kind == ArtifactKind.ManagedAssembly)
                .Sum(entry => entry.Size),
            Assemblies: assemblies);
    }

    /// <summary>
    /// Walks <paramref name="treeRoot"/> explicitly (never <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/>
    /// with a recursive option, which would follow reparse points), returning every
    /// regular file's "&lt;treeRoot-name&gt;/..." relative path. Fails closed and
    /// stops descending the instant any reparse point is found.
    /// </summary>
    private static bool TryEnumerateNormalTree(
        string treeRoot,
        out IReadOnlyDictionary<string, string> filesByRelativePath,
        out string? failureMessage)
    {
        var discovered = new Dictionary<string, string>(StringComparer.Ordinal);
        filesByRelativePath = discovered;
        failureMessage = null;

        if (!Directory.Exists(treeRoot))
        {
            // An absent reconstructed/logs directory is a path-set or containment
            // problem the caller surfaces itself (empty artifact manifests are valid;
            // callers with a non-empty manifest will fail the path-set check).
            return true;
        }

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(treeRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failureMessage = $"'{treeRoot}' could not be inspected.";
            return false;
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            failureMessage = $"'{treeRoot}' is a reparse point.";
            return false;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(treeRoot);

        while (pendingDirectories.Count > 0)
        {
            var current = pendingDirectories.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failureMessage = $"'{current}' could not be read.";
                return false;
            }

            foreach (var file in files.OrderBy(path => path, StringComparer.Ordinal))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failureMessage = $"'{file}' could not be inspected.";
                    return false;
                }

                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    failureMessage = $"'{file}' is a reparse point or not a regular file.";
                    return false;
                }

                var relative = Path.GetFileName(treeRoot) + "/" +
                    Path.GetRelativePath(treeRoot, file).Replace('\\', '/');
                if (!discovered.TryAdd(relative, file))
                {
                    failureMessage = $"'{relative}' is an ambiguous duplicate path.";
                    return false;
                }
            }

            foreach (var directory in directories)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failureMessage = $"'{directory}' could not be inspected.";
                    return false;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    failureMessage = $"'{directory}' is a reparse point.";
                    return false;
                }

                pendingDirectories.Push(directory);
            }
        }

        return true;
    }

    private static ValidatedExtractionIntegrity Valid() =>
        new(ValidatedExtractionIntegrityStatus.Valid, null, null);

    private static ValidatedExtractionIntegrity Missing(string code, string message) =>
        new(ValidatedExtractionIntegrityStatus.Missing, code, message);

    private static ValidatedExtractionIntegrity Incomplete(string code, string message) =>
        new(ValidatedExtractionIntegrityStatus.Incomplete, code, message);

    private static ValidatedExtractionIntegrity Mismatch(string code, string message) =>
        new(ValidatedExtractionIntegrityStatus.Mismatch, code, message);
}
