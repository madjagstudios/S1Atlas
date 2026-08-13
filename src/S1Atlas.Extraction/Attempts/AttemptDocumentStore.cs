using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Inputs;

namespace S1Atlas.Extraction.Attempts;

internal sealed record AttemptExecutionFacts(
    int OwnerProcessId,
    string ToolTrust,
    IReadOnlyDictionary<string, string> ResolvedInputPaths,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    InputManifest? PreInputManifest,
    InputManifest? PostInputManifest,
    TimeSpan Timeout);

internal sealed record AttemptDocumentSnapshot(
    ExtractionAttempt Attempt,
    AttemptExecutionFacts? ExecutionFacts);

internal sealed class AttemptDocumentStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] AttemptPropertyNamesV1 =
    [
        "schemaVersion", "attemptId", "recipeId", "buildId", "toolInstanceId",
        "toolTrust", "profileId", "profileVersion", "profileDigest",
        "validationPolicyId", "validationPolicyVersion", "validationPolicyDigest",
        "adapterVersion", "extractionSchemaVersion", "inputSource", "inputSnapshotId",
        "resolvedInputPaths", "arguments", "environmentOverrides", "preInputManifest",
        "postInputManifest", "timeoutSeconds", "ownerProcessId", "status", "createdAtUtc",
        "startedAtUtc", "completedAtUtc", "preInputManifestDigest",
        "postInputManifestDigest", "workingPath", "standardOutputPath", "standardErrorPath",
        "standardOutputTruncated", "standardErrorTruncated",
        "standardOutputDiscardedBytes", "standardErrorDiscardedBytes", "processId",
        "processExitCode", "failureStage", "failureCode", "failureMessage",
        "keepFailedArtifacts", "discardedFileCount", "discardedByteCount",
        "candidateOutputPath", "resultExtractionId"
    ];
    // Phase 4: schema 2 adds validationSourceExtractionId. Schema 1 (real Phase 3
    // documents already on disk) never carries this property; the writer now always
    // emits schema 2, and the reader accepts both shapes with an exact per-version
    // property set (see HasExactAttemptShape).
    private static readonly string[] AttemptPropertyNamesV2 =
        [.. AttemptPropertyNamesV1, "validationSourceExtractionId"];
    private static readonly string[] ManifestEntryPropertyNames =
        ["relativePath", "role", "size", "sha256", "lastWriteUtc"];
    private readonly Action<string, string>? _beforeReplace;

    public AttemptDocumentStore(Action<string, string>? beforeReplace = null)
    {
        _beforeReplace = beforeReplace;
    }

    public async Task WriteAsync(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt,
        AttemptExecutionFacts? executionFacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateAuthoritativeIdentity(paths, attempt);
        ValidateSchemaTwoInvariant(attempt);
        if (executionFacts is not null)
        {
            ValidateExecutionFacts(executionFacts);
        }
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(paths.AttemptRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(paths.DataRoot, paths.AttemptRoot);
        if (!PathSafety.IsNormalDirectory(paths.AttemptRoot))
        {
            throw new InvalidOperationException("The attempt document root is unsafe.");
        }

        var temporaryPath = $"{paths.AttemptDocumentPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            OwnedAttemptPaths.EnsureSafeExistingPath(
                paths.AttemptRoot, temporaryPath, allowFinalFile: true);
            var json = JsonSerializer.Serialize(ToDocument(attempt, executionFacts), JsonOptions);
            await WriteTemporaryAsync(temporaryPath, json, cancellationToken);
            _beforeReplace?.Invoke(temporaryPath, paths.AttemptDocumentPath);
            cancellationToken.ThrowIfCancellationRequested();
            OwnedAttemptPaths.EnsureSafeExistingPath(
                paths.AttemptRoot, paths.AttemptDocumentPath, allowFinalFile: true);
            File.Move(temporaryPath, paths.AttemptDocumentPath, overwrite: true);
        }
        finally
        {
            DeleteOwnedTemporaryBestEffort(paths, temporaryPath);
        }
    }

    public async Task<AttemptDocumentSnapshot?> TryReadAsync(
        OwnedAttemptPaths paths,
        ExtractionAttempt authoritativeAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(authoritativeAttempt);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAuthoritativeIdentity(paths, authoritativeAttempt);
            OwnedAttemptPaths.EnsureSafeExistingPath(
                paths.AttemptRoot, paths.AttemptDocumentPath, allowFinalFile: true);
            if (!PathSafety.TryObserveRegularFile(
                    paths.AttemptRoot, paths.AttemptDocumentPath, out _))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(
                paths.AttemptDocumentPath, Utf8NoBom, cancellationToken);
            using var parsed = JsonDocument.Parse(json);
            if (!HasExactAttemptShape(parsed.RootElement))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<AttemptDocument>(json, JsonOptions);
            var snapshot = FromDocument(document);
            return snapshot is not null && MatchesAuthoritativeIdentity(
                paths, authoritativeAttempt, snapshot.Attempt)
                ? snapshot
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private static AttemptDocument ToDocument(
        ExtractionAttempt attempt,
        AttemptExecutionFacts? facts) => new()
        {
            SchemaVersion = 2,
            AttemptId = attempt.AttemptId,
            RecipeId = attempt.RecipeId,
            BuildId = attempt.BuildId,
            ToolInstanceId = attempt.ToolInstanceId,
            ToolTrust = facts?.ToolTrust,
            ProfileId = attempt.ProfileId,
            ProfileVersion = attempt.ProfileVersion,
            ProfileDigest = attempt.ProfileDigest,
            ValidationPolicyId = attempt.ValidationPolicyId,
            ValidationPolicyVersion = attempt.ValidationPolicyVersion,
            ValidationPolicyDigest = attempt.ValidationPolicyDigest,
            AdapterVersion = attempt.AdapterVersion,
            ExtractionSchemaVersion = attempt.ExtractionSchemaVersion,
            InputSource = attempt.InputSource,
            InputSnapshotId = attempt.InputSnapshotId,
            ResolvedInputPaths = facts is null
            ? null
            : new(facts.ResolvedInputPaths, StringComparer.Ordinal),
            Arguments = facts is null ? null : [.. facts.Arguments],
            EnvironmentOverrides = facts is null
            ? null
            : new(facts.EnvironmentOverrides, StringComparer.Ordinal),
            PreInputManifest = ToDocument(facts?.PreInputManifest),
            PostInputManifest = ToDocument(facts?.PostInputManifest),
            TimeoutSeconds = facts is null ? null : checked((int)facts.Timeout.TotalSeconds),
            OwnerProcessId = facts?.OwnerProcessId,
            Status = attempt.Status,
            CreatedAtUtc = attempt.CreatedAtUtc,
            StartedAtUtc = attempt.StartedAtUtc,
            CompletedAtUtc = attempt.CompletedAtUtc,
            PreInputManifestDigest = attempt.PreInputManifestDigest,
            PostInputManifestDigest = attempt.PostInputManifestDigest,
            WorkingPath = attempt.WorkingPath,
            StandardOutputPath = attempt.StandardOutputPath,
            StandardErrorPath = attempt.StandardErrorPath,
            StandardOutputTruncated = attempt.StandardOutputTruncated,
            StandardErrorTruncated = attempt.StandardErrorTruncated,
            StandardOutputDiscardedBytes = attempt.StandardOutputDiscardedBytes,
            StandardErrorDiscardedBytes = attempt.StandardErrorDiscardedBytes,
            ProcessId = attempt.ProcessId,
            ProcessExitCode = attempt.ProcessExitCode,
            FailureStage = attempt.FailureStage,
            FailureCode = attempt.FailureCode,
            FailureMessage = SanitizeHumanMessage(attempt.FailureMessage),
            KeepFailedArtifacts = attempt.KeepFailedArtifacts,
            DiscardedFileCount = attempt.DiscardedFileCount,
            DiscardedByteCount = attempt.DiscardedByteCount,
            CandidateOutputPath = attempt.CandidateOutputPath,
            ResultExtractionId = attempt.ResultExtractionId,
            ValidationSourceExtractionId = attempt.ValidationSourceExtractionId
        };

    private static AttemptDocumentSnapshot? FromDocument(AttemptDocument? document)
    {
        if (document?.SchemaVersion is not (1 or 2) ||
            !OwnedAttemptPaths.IsLowerGuidN(document.AttemptId) ||
            !HasText(document.BuildId) || !HasText(document.ProfileId) ||
            document.ProfileVersion <= 0 || !IsLowerSha256(document.ProfileDigest) ||
            !HasText(document.ValidationPolicyId) || document.ValidationPolicyVersion <= 0 ||
            !IsLowerSha256(document.ValidationPolicyDigest) ||
            document.AdapterVersion <= 0 || document.ExtractionSchemaVersion <= 0 ||
            document.Status is null ||
            document.CreatedAtUtc is null || !HasText(document.WorkingPath) ||
            !HasText(document.StandardOutputPath) || !HasText(document.StandardErrorPath) ||
            document.StandardOutputTruncated is null || document.StandardErrorTruncated is null ||
            document.StandardOutputDiscardedBytes is null or < 0 ||
            document.StandardErrorDiscardedBytes is null or < 0 ||
            document.KeepFailedArtifacts is null || document.DiscardedFileCount is null or < 0 ||
            document.DiscardedByteCount is null or < 0 ||
            !TryManifest(document.PreInputManifest, out var preManifest) ||
            !TryManifest(document.PostInputManifest, out var postManifest))
        {
            return null;
        }

        var executionFactsAbsent = document.ToolTrust is null &&
            document.ResolvedInputPaths is null && document.Arguments is null &&
            document.EnvironmentOverrides is null && document.TimeoutSeconds is null &&
            document.OwnerProcessId is null;
        var executionFactsValid = HasText(document.ToolTrust) &&
            document.ResolvedInputPaths is not null &&
            document.ResolvedInputPaths.All(pair =>
                !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) &&
            document.Arguments is not null && document.Arguments.All(item => !string.IsNullOrEmpty(item)) &&
            document.EnvironmentOverrides is not null &&
            document.EnvironmentOverrides.All(pair => !string.IsNullOrWhiteSpace(pair.Key)) &&
            document.TimeoutSeconds is > 0 && document.OwnerProcessId is >= 0;
        if ((!executionFactsAbsent && !executionFactsValid) ||
            document.FailureMessage?.Contains('\n') == true ||
            document.FailureMessage?.Contains('\r') == true)
        {
            return null;
        }

        // Phase 4: schema 1 documents structurally never carry
        // validationSourceExtractionId (HasExactAttemptShape already enforced the
        // exact schema-1 property set), so it deserializes to null; this is a
        // defense-in-depth restatement of that mapping. Schema 2 documents may carry
        // either a candidate output path or a validation source extraction ID, but
        // never both.
        if ((document.SchemaVersion == 1 && document.ValidationSourceExtractionId is not null) ||
            (document.SchemaVersion == 2 &&
             HasText(document.CandidateOutputPath) &&
             HasText(document.ValidationSourceExtractionId)))
        {
            return null;
        }

        var attempt = new ExtractionAttempt(
            document.AttemptId!, document.RecipeId, document.BuildId!, document.ToolInstanceId,
            document.ProfileId!, document.ProfileVersion, document.ProfileDigest!,
            document.ValidationPolicyId!, document.ValidationPolicyVersion,
            document.ValidationPolicyDigest!, document.AdapterVersion,
            document.ExtractionSchemaVersion, document.InputSource, document.InputSnapshotId,
            document.Status.Value, document.CreatedAtUtc.Value, document.StartedAtUtc,
            document.CompletedAtUtc, document.PreInputManifestDigest,
            document.PostInputManifestDigest, document.WorkingPath!,
            document.StandardOutputPath!, document.StandardErrorPath!,
            document.StandardOutputTruncated.Value, document.StandardErrorTruncated.Value,
            document.StandardOutputDiscardedBytes.Value,
            document.StandardErrorDiscardedBytes.Value, document.ProcessId,
            document.ProcessExitCode, document.FailureStage, document.FailureCode,
            document.FailureMessage, document.KeepFailedArtifacts.Value,
            document.DiscardedFileCount.Value, document.DiscardedByteCount.Value,
            document.CandidateOutputPath, document.ResultExtractionId,
            document.ValidationSourceExtractionId);
        var facts = executionFactsAbsent
            ? null
            : new AttemptExecutionFacts(
                document.OwnerProcessId!.Value, document.ToolTrust!,
                new Dictionary<string, string>(document.ResolvedInputPaths!, StringComparer.Ordinal),
                document.Arguments!.Select(item => item!).ToList(),
                new Dictionary<string, string?>(document.EnvironmentOverrides!, StringComparer.Ordinal),
                preManifest, postManifest, TimeSpan.FromSeconds(document.TimeoutSeconds!.Value));
        return new AttemptDocumentSnapshot(attempt, facts);
    }

    private static bool MatchesAuthoritativeIdentity(
        OwnedAttemptPaths paths,
        ExtractionAttempt authoritative,
        ExtractionAttempt document) =>
        document.AttemptId == authoritative.AttemptId &&
        document.BuildId == authoritative.BuildId &&
        document.RecipeId == authoritative.RecipeId &&
        document.ToolInstanceId == authoritative.ToolInstanceId &&
        document.ProfileId == authoritative.ProfileId &&
        document.ProfileVersion == authoritative.ProfileVersion &&
        document.ProfileDigest == authoritative.ProfileDigest &&
        document.ValidationPolicyId == authoritative.ValidationPolicyId &&
        document.ValidationPolicyVersion == authoritative.ValidationPolicyVersion &&
        document.ValidationPolicyDigest == authoritative.ValidationPolicyDigest &&
        document.AdapterVersion == authoritative.AdapterVersion &&
        document.ExtractionSchemaVersion == authoritative.ExtractionSchemaVersion &&
        document.WorkingPath == authoritative.WorkingPath &&
        document.StandardOutputPath == authoritative.StandardOutputPath &&
        document.StandardErrorPath == authoritative.StandardErrorPath &&
        document.AttemptId == paths.AttemptId;

    private static void ValidateAuthoritativeIdentity(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        if (attempt.AttemptId != paths.AttemptId || attempt.BuildId != paths.BuildId ||
            !OwnedAttemptPaths.IsLowerGuidN(attempt.AttemptId))
        {
            throw new InvalidOperationException(
                "The attempt does not match its owned filesystem paths.");
        }

        if (!OwnedAttemptPaths.IsSameOrDescendant(paths.StagingRoot, attempt.WorkingPath) ||
            !OwnedAttemptPaths.IsSameOrDescendant(paths.AttemptRoot, attempt.StandardOutputPath) ||
            !OwnedAttemptPaths.IsSameOrDescendant(paths.AttemptRoot, attempt.StandardErrorPath))
        {
            throw new InvalidOperationException(
                "The attempt contains paths outside its owned roots.");
        }
    }

    private static void ValidateSchemaTwoInvariant(ExtractionAttempt attempt)
    {
        if (!string.IsNullOrWhiteSpace(attempt.CandidateOutputPath) &&
            !string.IsNullOrWhiteSpace(attempt.ValidationSourceExtractionId))
        {
            throw new InvalidOperationException(
                "An attempt document cannot carry both a candidate output path and " +
                "a validation source extraction ID.");
        }
    }

    private static void ValidateExecutionFacts(AttemptExecutionFacts facts)
    {
        if (facts.OwnerProcessId < 0 || string.IsNullOrWhiteSpace(facts.ToolTrust) ||
            facts.ResolvedInputPaths.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            facts.Arguments.Any(string.IsNullOrEmpty) ||
            facts.EnvironmentOverrides.Any(pair => string.IsNullOrWhiteSpace(pair.Key)) ||
            facts.Timeout <= TimeSpan.Zero || facts.Timeout.TotalSeconds > int.MaxValue ||
            facts.Timeout.TotalSeconds != Math.Truncate(facts.Timeout.TotalSeconds))
        {
            throw new ArgumentException("The attempt execution facts are invalid.", nameof(facts));
        }
    }

    private static ManifestDocument? ToDocument(InputManifest? manifest) => manifest is null
        ? null
        : new ManifestDocument
        {
            Files = manifest.Entries.Select(entry => (ManifestEntryDocument?)new ManifestEntryDocument
            {
                RelativePath = entry.RelativePath,
                Role = entry.Role,
                Size = entry.Size,
                Sha256 = entry.Sha256,
                LastWriteUtc = entry.LastWriteUtc
            }).ToList()
        };

    private static bool TryManifest(
        ManifestDocument? document,
        out InputManifest? manifest)
    {
        manifest = null;
        if (document is null)
        {
            return true;
        }

        if (document.Files is null)
        {
            return false;
        }

        var entries = new List<InputManifestEntry>(document.Files.Count);
        foreach (var file in document.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) ||
                Path.IsPathRooted(file.RelativePath) || file.RelativePath.Contains("..", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(file.Role) || file.Size is null or < 0 ||
                !IsLowerSha256(file.Sha256) || file.LastWriteUtc is null)
            {
                return false;
            }

            entries.Add(new InputManifestEntry(
                file.RelativePath, file.Role, file.Size.Value, file.Sha256!, file.LastWriteUtc.Value));
        }

        manifest = new InputManifest(entries);
        return true;
    }

    private static async Task WriteTemporaryAsync(
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteOwnedTemporaryBestEffort(
        OwnedAttemptPaths paths,
        string temporaryPath)
    {
        try
        {
            if (OwnedAttemptPaths.IsSameOrDescendant(paths.AttemptRoot, temporaryPath) &&
                Path.GetDirectoryName(temporaryPath) == paths.AttemptRoot &&
                Path.GetFileName(temporaryPath).StartsWith("attempt.json.", StringComparison.Ordinal) &&
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

    private static string? SanitizeHumanMessage(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(firstLine)
            ? null
            : firstLine[..Math.Min(firstLine.Length, 2048)];
    }

    private static bool HasText([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value);

    private static bool IsLowerSha256([NotNullWhen(true)] string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasExactAttemptShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            return false;
        }

        var expectedProperties = schemaVersion switch
        {
            1 => AttemptPropertyNamesV1,
            2 => AttemptPropertyNamesV2,
            _ => null
        };
        if (expectedProperties is null || !HasExactProperties(root, expectedProperties))
        {
            return false;
        }

        return HasExactManifestShape(root.GetProperty("preInputManifest")) &&
            HasExactManifestShape(root.GetProperty("postInputManifest"));
    }

    private static bool HasExactManifestShape(JsonElement manifest)
    {
        if (manifest.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!HasExactProperties(manifest, ["files"]) ||
            manifest.GetProperty("files").ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return manifest.GetProperty("files").EnumerateArray()
            .All(entry => HasExactProperties(entry, ManifestEntryPropertyNames));
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
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class AttemptDocument
    {
        public int SchemaVersion { get; init; }
        public string? AttemptId { get; init; }
        public string? RecipeId { get; init; }
        public string? BuildId { get; init; }
        public string? ToolInstanceId { get; init; }
        public string? ToolTrust { get; init; }
        public string? ProfileId { get; init; }
        public int ProfileVersion { get; init; }
        public string? ProfileDigest { get; init; }
        public string? ValidationPolicyId { get; init; }
        public int ValidationPolicyVersion { get; init; }
        public string? ValidationPolicyDigest { get; init; }
        public int AdapterVersion { get; init; }
        public int ExtractionSchemaVersion { get; init; }
        public ExtractionInputSource? InputSource { get; init; }
        public string? InputSnapshotId { get; init; }
        public Dictionary<string, string>? ResolvedInputPaths { get; init; }
        public List<string?>? Arguments { get; init; }
        public Dictionary<string, string?>? EnvironmentOverrides { get; init; }
        public ManifestDocument? PreInputManifest { get; init; }
        public ManifestDocument? PostInputManifest { get; init; }
        public int? TimeoutSeconds { get; init; }
        public int? OwnerProcessId { get; init; }
        public ExtractionAttemptStatus? Status { get; init; }
        public DateTimeOffset? CreatedAtUtc { get; init; }
        public DateTimeOffset? StartedAtUtc { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; init; }
        public string? PreInputManifestDigest { get; init; }
        public string? PostInputManifestDigest { get; init; }
        public string? WorkingPath { get; init; }
        public string? StandardOutputPath { get; init; }
        public string? StandardErrorPath { get; init; }
        public bool? StandardOutputTruncated { get; init; }
        public bool? StandardErrorTruncated { get; init; }
        public long? StandardOutputDiscardedBytes { get; init; }
        public long? StandardErrorDiscardedBytes { get; init; }
        public int? ProcessId { get; init; }
        public int? ProcessExitCode { get; init; }
        public ExtractionFailureStage? FailureStage { get; init; }
        public ExtractionFailureCode? FailureCode { get; init; }
        public string? FailureMessage { get; init; }
        public bool? KeepFailedArtifacts { get; init; }
        public int? DiscardedFileCount { get; init; }
        public long? DiscardedByteCount { get; init; }
        public string? CandidateOutputPath { get; init; }
        public string? ResultExtractionId { get; init; }
        public string? ValidationSourceExtractionId { get; init; }
    }

    private sealed class ManifestDocument
    {
        public List<ManifestEntryDocument?>? Files { get; init; }
    }

    private sealed class ManifestEntryDocument
    {
        public string? RelativePath { get; init; }
        public string? Role { get; init; }
        public long? Size { get; init; }
        public string? Sha256 { get; init; }
        public DateTimeOffset? LastWriteUtc { get; init; }
    }
}
