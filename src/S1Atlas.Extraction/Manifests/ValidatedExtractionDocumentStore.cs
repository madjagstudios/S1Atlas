using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Promotion;

namespace S1Atlas.Extraction.Manifests;

/// <summary>The outcome of writing the four final, immutable validated-extraction documents.</summary>
internal sealed record ValidatedExtractionDocumentWriteResult(
    ValidatedExtractionDocumentPaths Paths,
    string ArtifactManifestDigest,
    string ExtractionId);

/// <summary>
/// Reads and writes the strict, immutable validated-extraction documents:
/// <c>extraction.json</c>, <c>artifact-manifest.json</c>, <c>validation.json</c>, and
/// <c>complete.marker</c> in a final extraction (or promotion-staging) root, plus the
/// attempt-level <c>validation.json</c> strict report projection. Writes are atomic
/// (owned sibling temp file, flushed to disk, non-overwriting move); the marker is
/// always written last, after the three documents it binds are durable on disk.
/// Reads never deserialize untrusted bytes directly into a Core record: every read
/// first proves an exact schema-1 property shape (rejecting unknown or duplicate
/// properties) via <see cref="JsonDocument"/>, then maps the resulting DTO into
/// domain facts through <see cref="ValidatedExtractionDocumentMapper"/>.
/// </summary>
internal sealed class ValidatedExtractionDocumentStore
{
    public async Task WriteAttemptValidationReportAsync(
        OwnedValidatedExtractionPaths attemptPaths,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptPaths);
        ArgumentNullException.ThrowIfNull(report);
        if (attemptPaths.AttemptId is null || attemptPaths.AttemptValidationReportPath is null)
        {
            throw new ArgumentException(
                "The paths must be attempt-scoped (created via OwnedValidatedExtractionPaths.ForAttempt).",
                nameof(attemptPaths));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The attempt-level validation.json is always a direct sibling of attempt.json:
        // "<attempt-root>/validation.json". This is the exact convention
        // SqliteAtlasRepository.ValidatedExtractions.DeriveValidationReportPath derives
        // independently from the attempt's standard-output log path, so the two must
        // never diverge.
        var attemptRoot = Path.GetDirectoryName(attemptPaths.AttemptValidationReportPath)
            ?? throw new InvalidOperationException(
                "The attempt validation report path has no parent directory.");

        Directory.CreateDirectory(attemptRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(attemptPaths.DataRoot, attemptRoot);
        if (!PathSafety.IsNormalDirectory(attemptRoot))
        {
            throw new InvalidOperationException("The attempt root is unsafe.");
        }

        var document = ValidatedExtractionDocumentMapper.ToValidationReportDocument(report);
        await WriteJsonAtomicAsync(
            attemptRoot, attemptPaths.AttemptValidationReportPath, document, cancellationToken);
    }

    public async Task<ValidatedExtractionDocumentWriteResult> WriteFinalDocumentsAsync(
        string dataRoot,
        string documentsRoot,
        ValidatedExtraction extraction,
        ArtifactManifest manifest,
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsRoot);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var target = Path.GetFullPath(documentsRoot);
        if (!OwnedAttemptPaths.IsSameOrDescendant(root, target) ||
            string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The validated extraction documents root escapes its Atlas data root.");
        }

        Directory.CreateDirectory(target);
        OwnedAttemptPaths.EnsureSafeExistingPath(root, target);
        if (!PathSafety.IsNormalDirectory(target))
        {
            throw new InvalidOperationException("The validated extraction documents root is unsafe.");
        }

        // Recompute the artifact digest and extraction ID from the manifest itself
        // rather than trusting the caller-supplied extraction/report values verbatim:
        // the marker must bind values that are actually correct, regardless of any
        // caller bug in the record it built.
        var recomputedDigest = ArtifactManifestFingerprint.Create(manifest);
        if (!string.Equals(recomputedDigest, extraction.ArtifactManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(recomputedDigest, report.ArtifactManifestDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The artifact manifest digest does not match the extraction or validation report.");
        }

        var recomputedExtractionId = ExtractionId.Create(extraction.RecipeId, recomputedDigest);
        if (!string.Equals(recomputedExtractionId, extraction.ExtractionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The extraction ID does not match its recipe ID and artifact manifest digest.");
        }

        var paths = ValidatedExtractionDocumentPaths.ForRoot(target);

        var extractionDocument = ValidatedExtractionDocumentMapper.ToExtractionDocument(extraction);
        var extractionSha256 = await WriteJsonAtomicAsync(
            target, paths.ExtractionJsonPath, extractionDocument, cancellationToken);

        var manifestDocument = ValidatedExtractionDocumentMapper.ToArtifactManifestDocument(
            manifest, recomputedDigest);
        var artifactManifestSha256 = await WriteJsonAtomicAsync(
            target, paths.ArtifactManifestJsonPath, manifestDocument, cancellationToken);

        var reportDocument = ValidatedExtractionDocumentMapper.ToValidationReportDocument(report);
        var validationSha256 = await WriteJsonAtomicAsync(
            target, paths.ValidationJsonPath, reportDocument, cancellationToken);

        var marker = new CompleteMarkerContent(
            SchemaVersion: ValidatedExtractionDocumentSchema.SchemaVersion,
            ExtractionId: recomputedExtractionId,
            ArtifactManifestDigest: recomputedDigest,
            ExtractionDocumentSha256: extractionSha256,
            ArtifactManifestDocumentSha256: artifactManifestSha256,
            ValidationDocumentSha256: validationSha256);
        var markerDocument = ValidatedExtractionDocumentMapper.ToCompleteMarkerDocument(marker);
        await WriteJsonAtomicAsync(target, paths.CompleteMarkerPath, markerDocument, cancellationToken);

        return new ValidatedExtractionDocumentWriteResult(paths, recomputedDigest, recomputedExtractionId);
    }

    public async Task<ExtractionDocumentContent?> TryReadExtractionAsync(
        string path, CancellationToken cancellationToken)
    {
        var raw = await TryReadRawAsync(path, cancellationToken);
        if (raw is null)
        {
            return null;
        }

        return TryParse<ExtractionDocument, ExtractionDocumentContent>(
            raw.Value.Json,
            ValidatedExtractionDocumentSchema.HasExactExtractionShape,
            ValidatedExtractionDocumentMapper.FromExtractionDocument);
    }

    public async Task<ArtifactManifestDocumentContent?> TryReadArtifactManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        var raw = await TryReadRawAsync(path, cancellationToken);
        if (raw is null)
        {
            return null;
        }

        return TryParse<ArtifactManifestDocument, ArtifactManifestDocumentContent>(
            raw.Value.Json,
            ValidatedExtractionDocumentSchema.HasExactArtifactManifestShape,
            ValidatedExtractionDocumentMapper.FromArtifactManifestDocument);
    }

    public async Task<ValidationReport?> TryReadValidationReportAsync(
        string path, CancellationToken cancellationToken)
    {
        var raw = await TryReadRawAsync(path, cancellationToken);
        if (raw is null)
        {
            return null;
        }

        return TryParse<ValidationReportDocument, ValidationReport>(
            raw.Value.Json,
            ValidatedExtractionDocumentSchema.HasExactValidationReportShape,
            ValidatedExtractionDocumentMapper.FromValidationReportDocument);
    }

    public async Task<CompleteMarkerContent?> TryReadCompleteMarkerAsync(
        string path, CancellationToken cancellationToken)
    {
        var raw = await TryReadRawAsync(path, cancellationToken);
        if (raw is null)
        {
            return null;
        }

        return TryParse<CompleteMarkerDocument, CompleteMarkerContent>(
            raw.Value.Json,
            ValidatedExtractionDocumentSchema.HasExactCompleteMarkerShape,
            ValidatedExtractionDocumentMapper.FromCompleteMarkerDocument);
    }

    /// <summary>Reads and strictly validates all four documents rooted at <paramref name="documentsRoot"/>. Returns null if any is missing or malformed (an "Incomplete" root).</summary>
    public async Task<ValidatedExtractionDocumentBundle?> TryReadFinalDocumentsAsync(
        string documentsRoot, CancellationToken cancellationToken)
    {
        var paths = ValidatedExtractionDocumentPaths.ForRoot(documentsRoot);
        var extraction = await TryReadExtractionAsync(paths.ExtractionJsonPath, cancellationToken);
        var manifest = await TryReadArtifactManifestAsync(paths.ArtifactManifestJsonPath, cancellationToken);
        var report = await TryReadValidationReportAsync(paths.ValidationJsonPath, cancellationToken);
        var marker = await TryReadCompleteMarkerAsync(paths.CompleteMarkerPath, cancellationToken);
        if (extraction is null || manifest is null || report is null || marker is null)
        {
            return null;
        }

        return new ValidatedExtractionDocumentBundle(extraction, manifest, report, marker, paths);
    }

    /// <summary>Computes the SHA-256 of a document's exact on-disk bytes, for comparison against <c>complete.marker</c>.</summary>
    public async Task<string?> TryComputeDocumentSha256Async(string path, CancellationToken cancellationToken)
    {
        var raw = await TryReadRawAsync(path, cancellationToken);
        return raw is null ? null : ToHex(SHA256.HashData(raw.Value.Bytes));
    }

    private static T? TryParse<TDocument, T>(
        string json, Func<JsonElement, bool> hasExactShape, Func<TDocument?, T?> map)
        where T : class
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (!hasExactShape(parsed.RootElement))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<TDocument>(json, ValidatedExtractionDocumentSchema.JsonOptions);
            return map(document);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or
            NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private static async Task<(byte[] Bytes, string Json)?> TryReadRawAsync(
        string path, CancellationToken cancellationToken)
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

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var json = ValidatedExtractionDocumentSchema.Utf8NoBom.GetString(bytes);
            return (bytes, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string> WriteJsonAtomicAsync<TDocument>(
        string containingRoot,
        string finalPath,
        TDocument document,
        CancellationToken cancellationToken)
    {
        OwnedAttemptPaths.EnsureSafeExistingPath(containingRoot, finalPath, allowFinalFile: true);

        var json = JsonSerializer.Serialize(document, ValidatedExtractionDocumentSchema.JsonOptions);
        var bytes = ValidatedExtractionDocumentSchema.Utf8NoBom.GetBytes(json);
        var temporaryPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        OwnedAttemptPaths.EnsureSafeExistingPath(containingRoot, temporaryPath, allowFinalFile: true);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            OwnedAttemptPaths.EnsureSafeExistingPath(containingRoot, finalPath, allowFinalFile: true);
            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            DeleteTemporaryBestEffort(containingRoot, temporaryPath, finalPath);
        }

        return ToHex(SHA256.HashData(bytes));
    }

    private static void DeleteTemporaryBestEffort(
        string containingRoot, string temporaryPath, string finalPath)
    {
        try
        {
            var expectedPrefix = Path.GetFileName(finalPath) + ".";
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(containingRoot));
            if (OwnedAttemptPaths.IsSameOrDescendant(containingRoot, temporaryPath) &&
                Path.GetDirectoryName(temporaryPath) == normalizedRoot &&
                Path.GetFileName(temporaryPath).StartsWith(expectedPrefix, StringComparison.Ordinal) &&
                Path.GetFileName(temporaryPath).EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
