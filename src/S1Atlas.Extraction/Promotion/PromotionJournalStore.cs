using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Inputs;

namespace S1Atlas.Extraction.Promotion;

/// <summary>
/// The strict content of a sibling promotion journal
/// (<c>builds/&lt;build&gt;/extractions/.staging/&lt;attempt-id&gt;.promotion.json</c>).
/// The journal is written before any output is staged, remains after a post-rename
/// database failure, and is deleted only after successful database registration or
/// recovery. It is a sibling of the staging directory and never a file copied into
/// the final immutable output.
/// </summary>
internal sealed record PromotionJournalContent(
    int SchemaVersion,
    string AttemptId,
    string BuildId,
    string RecipeId,
    string PlannedExtractionId,
    string ArtifactManifestDigest,
    string SourceCandidatePath,
    string StagingPath,
    string FinalExtractionPath,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Reads, writes, and deletes the strict promotion journal. Writes are atomic (owned
/// sibling temp file, flushed to disk, non-overwriting move). Reads fail closed: a
/// present but malformed journal throws rather than being silently ignored, so
/// recovery can never guess at ambiguous evidence.
/// </summary>
internal sealed class PromotionJournalStore
{
    public const int SchemaVersion = 1;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static readonly string[] PropertyNames =
    [
        "schemaVersion", "attemptId", "buildId", "recipeId", "plannedExtractionId",
        "artifactManifestDigest", "sourceCandidatePath", "stagingPath",
        "finalExtractionPath", "createdAtUtc"
    ];

    public async Task WriteAsync(
        OwnedValidatedExtractionPaths attemptPaths,
        PromotionJournalContent journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptPaths);
        ArgumentNullException.ThrowIfNull(journal);
        if (attemptPaths.PromotionJournalPath is null || attemptPaths.PromotionStagingRoot is null)
        {
            throw new ArgumentException(
                "The paths must be attempt-scoped (created via OwnedValidatedExtractionPaths.ForAttempt).",
                nameof(attemptPaths));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stagingParent = Path.GetDirectoryName(attemptPaths.PromotionStagingRoot)
            ?? throw new InvalidOperationException(
                "The promotion staging root has no parent directory.");
        Directory.CreateDirectory(stagingParent);
        OwnedAttemptPaths.EnsureSafeExistingPath(attemptPaths.DataRoot, stagingParent);
        if (!PathSafety.IsNormalDirectory(stagingParent))
        {
            throw new InvalidOperationException("The promotion journal directory is unsafe.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            attemptPaths.DataRoot, attemptPaths.PromotionJournalPath, allowFinalFile: true);

        var document = ToDocument(journal);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var bytes = Utf8NoBom.GetBytes(json);
        var temporaryPath = $"{attemptPaths.PromotionJournalPath}.{Guid.NewGuid():N}.tmp";
        OwnedAttemptPaths.EnsureSafeExistingPath(
            attemptPaths.DataRoot, temporaryPath, allowFinalFile: true);

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
            File.Move(temporaryPath, attemptPaths.PromotionJournalPath, overwrite: false);
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// Reads and strictly validates the journal at <paramref name="journalPath"/>.
    /// Returns <see langword="null"/> only when the file does not exist. A present but
    /// malformed, reparse-point, or schema-invalid journal throws
    /// <see cref="InvalidDataException"/> so recovery fails closed on ambiguity.
    /// </summary>
    public async Task<PromotionJournalContent?> TryReadAsync(
        string journalPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(journalPath))
        {
            return null;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(journalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The promotion journal '{journalPath}' could not be inspected.", exception);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The promotion journal '{journalPath}' is a directory or reparse point.");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The promotion journal '{journalPath}' could not be read.", exception);
        }

        var content = TryParse(Utf8NoBom.GetString(bytes));
        if (content is null)
        {
            throw new InvalidDataException(
                $"The promotion journal '{journalPath}' is malformed or does not match its strict schema.");
        }

        return content;
    }

    public void DeleteBestEffort(string journalPath)
    {
        try
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static PromotionJournalContent? TryParse(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (!HasExactProperties(parsed.RootElement, PropertyNames))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<PromotionJournalDocument>(json, JsonOptions);
            return FromDocument(document);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or
            NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private static bool HasExactProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Length &&
            actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static PromotionJournalDocument ToDocument(PromotionJournalContent journal) => new()
    {
        SchemaVersion = SchemaVersion,
        AttemptId = journal.AttemptId,
        BuildId = journal.BuildId,
        RecipeId = journal.RecipeId,
        PlannedExtractionId = journal.PlannedExtractionId,
        ArtifactManifestDigest = journal.ArtifactManifestDigest,
        SourceCandidatePath = journal.SourceCandidatePath,
        StagingPath = journal.StagingPath,
        FinalExtractionPath = journal.FinalExtractionPath,
        CreatedAtUtc = journal.CreatedAtUtc
    };

    private static PromotionJournalContent? FromDocument(PromotionJournalDocument? document)
    {
        if (document is null || document.SchemaVersion != SchemaVersion ||
            !HasText(document.AttemptId) || !HasText(document.BuildId) ||
            !HasText(document.RecipeId) || !HasText(document.PlannedExtractionId) ||
            !HasText(document.ArtifactManifestDigest) || !HasText(document.SourceCandidatePath) ||
            !HasText(document.StagingPath) || !HasText(document.FinalExtractionPath) ||
            document.CreatedAtUtc is null)
        {
            return null;
        }

        return new PromotionJournalContent(
            document.SchemaVersion, document.AttemptId!, document.BuildId!, document.RecipeId!,
            document.PlannedExtractionId!, document.ArtifactManifestDigest!,
            document.SourceCandidatePath!, document.StagingPath!, document.FinalExtractionPath!,
            document.CreatedAtUtc.Value);
    }

    private static void TryDeleteTemporary(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
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

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        return options;
    }

    private sealed class PromotionJournalDocument
    {
        public int SchemaVersion { get; init; }
        public string? AttemptId { get; init; }
        public string? BuildId { get; init; }
        public string? RecipeId { get; init; }
        public string? PlannedExtractionId { get; init; }
        public string? ArtifactManifestDigest { get; init; }
        public string? SourceCandidatePath { get; init; }
        public string? StagingPath { get; init; }
        public string? FinalExtractionPath { get; init; }
        public DateTimeOffset? CreatedAtUtc { get; init; }
    }
}
