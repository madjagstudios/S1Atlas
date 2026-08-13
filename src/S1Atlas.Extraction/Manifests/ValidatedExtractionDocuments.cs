using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Manifests;

/// <summary>
/// Strict schema-version-1 shapes for the four immutable validated-extraction
/// documents (<c>extraction.json</c>, <c>artifact-manifest.json</c>,
/// <c>validation.json</c>, <c>complete.marker</c>), their JSON options, exact-shape
/// validators (rejecting unknown or duplicate properties), and mappers between the
/// wire DTOs and domain facts. Untrusted JSON is never deserialized directly into a
/// Core record: every read goes through a DTO here first, is shape- and
/// content-validated, and only then mapped into domain types.
/// </summary>
internal static class ValidatedExtractionDocumentSchema
{
    public const int SchemaVersion = 1;

    public static readonly UTF8Encoding Utf8NoBom = new(false);
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static readonly string[] ExtractionPropertyNames =
    [
        "schemaVersion", "extractionId", "recipeId", "sourceAttemptId", "buildId",
        "toolInstanceId", "profileId", "profileVersion", "profileDigest", "adapterVersion",
        "extractionSchemaVersion", "artifactManifestDigest", "createdAtUtc", "trustLevel",
        "initialValidationOutcome", "statistics"
    ];

    public static readonly string[] StatisticsPropertyNames =
    [
        "artifactCount", "libraryCount", "managedAssemblyCount", "typeDefinitionCount",
        "methodDefinitionCount", "fieldDefinitionCount", "propertyDefinitionCount",
        "eventDefinitionCount", "totalOutputBytes", "totalManagedBytes", "assemblies"
    ];

    public static readonly string[] AssemblyIdentityStatisticsPropertyNames =
    [
        "assemblyName", "fileCount", "managedBytes", "typeDefinitionCount",
        "methodDefinitionCount", "fieldDefinitionCount", "propertyDefinitionCount",
        "eventDefinitionCount"
    ];

    public static readonly string[] ArtifactManifestPropertyNames =
        ["schemaVersion", "artifactManifestDigest", "entries"];

    public static readonly string[] ArtifactManifestEntryPropertyNames =
    [
        "relativePath", "kind", "size", "sha256", "assemblyName", "moduleName",
        "typeDefinitionCount", "methodDefinitionCount", "fieldDefinitionCount",
        "propertyDefinitionCount", "eventDefinitionCount"
    ];

    public static readonly string[] ValidationReportPropertyNames =
    [
        "schemaVersion", "attemptId", "subjectKind", "subjectExtractionId", "buildId",
        "recipeId", "policyId", "policyVersion", "policyDigest", "outcome",
        "inputIntegrityPassed", "processIntegrityPassed", "outputContainmentPassed",
        "artifactManifestDigest", "statistics", "baselineExtractionId", "comparisons",
        "issues", "preferenceEligible", "validatedAtUtc"
    ];

    public static readonly string[] MetricComparisonPropertyNames =
        ["metric", "assemblyName", "baselineValue", "candidateValue", "relativeChange", "classification"];

    public static readonly string[] IssuePropertyNames =
        ["severity", "code", "message", "artifactRelativePath", "preferenceBlocking"];

    public static readonly string[] CompleteMarkerPropertyNames =
    [
        "schemaVersion", "extractionId", "artifactManifestDigest", "extractionDocumentSha256",
        "artifactManifestDocumentSha256", "validationDocumentSha256"
    ];

    public static bool HasExactExtractionShape(JsonElement root) =>
        HasExactProperties(root, ExtractionPropertyNames) &&
        HasExactStatisticsShape(root.GetProperty("statistics"));

    public static bool HasExactStatisticsShape(JsonElement statistics)
    {
        if (!HasExactProperties(statistics, StatisticsPropertyNames))
        {
            return false;
        }

        var assemblies = statistics.GetProperty("assemblies");
        return assemblies.ValueKind == JsonValueKind.Array &&
            assemblies.EnumerateArray().All(
                entry => HasExactProperties(entry, AssemblyIdentityStatisticsPropertyNames));
    }

    public static bool HasExactArtifactManifestShape(JsonElement root)
    {
        if (!HasExactProperties(root, ArtifactManifestPropertyNames))
        {
            return false;
        }

        var entries = root.GetProperty("entries");
        return entries.ValueKind == JsonValueKind.Array &&
            entries.EnumerateArray().All(
                entry => HasExactProperties(entry, ArtifactManifestEntryPropertyNames));
    }

    public static bool HasExactValidationReportShape(JsonElement root)
    {
        if (!HasExactProperties(root, ValidationReportPropertyNames) ||
            !HasExactStatisticsShape(root.GetProperty("statistics")))
        {
            return false;
        }

        var comparisons = root.GetProperty("comparisons");
        var issues = root.GetProperty("issues");
        return comparisons.ValueKind == JsonValueKind.Array &&
            comparisons.EnumerateArray().All(
                entry => HasExactProperties(entry, MetricComparisonPropertyNames)) &&
            issues.ValueKind == JsonValueKind.Array &&
            issues.EnumerateArray().All(entry => HasExactProperties(entry, IssuePropertyNames));
    }

    public static bool HasExactCompleteMarkerShape(JsonElement root) =>
        HasExactProperties(root, CompleteMarkerPropertyNames);

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
}

// -- Wire DTOs -------------------------------------------------------------

internal sealed class ExtractionDocument
{
    public int SchemaVersion { get; init; }
    public string? ExtractionId { get; init; }
    public string? RecipeId { get; init; }
    public string? SourceAttemptId { get; init; }
    public string? BuildId { get; init; }
    public string? ToolInstanceId { get; init; }
    public string? ProfileId { get; init; }
    public int ProfileVersion { get; init; }
    public string? ProfileDigest { get; init; }
    public int AdapterVersion { get; init; }
    public int ExtractionSchemaVersion { get; init; }
    public string? ArtifactManifestDigest { get; init; }
    public DateTimeOffset? CreatedAtUtc { get; init; }
    public ToolTrustLevel? TrustLevel { get; init; }
    public ValidationOutcome? InitialValidationOutcome { get; init; }
    public StatisticsDocument? Statistics { get; init; }
}

internal sealed class StatisticsDocument
{
    public int ArtifactCount { get; init; }
    public int LibraryCount { get; init; }
    public int ManagedAssemblyCount { get; init; }
    public int TypeDefinitionCount { get; init; }
    public int MethodDefinitionCount { get; init; }
    public int FieldDefinitionCount { get; init; }
    public int PropertyDefinitionCount { get; init; }
    public int EventDefinitionCount { get; init; }
    public long TotalOutputBytes { get; init; }
    public long TotalManagedBytes { get; init; }
    public List<AssemblyIdentityStatisticsDocument?>? Assemblies { get; init; }
}

internal sealed class AssemblyIdentityStatisticsDocument
{
    public string? AssemblyName { get; init; }
    public int FileCount { get; init; }
    public long ManagedBytes { get; init; }
    public int TypeDefinitionCount { get; init; }
    public int MethodDefinitionCount { get; init; }
    public int FieldDefinitionCount { get; init; }
    public int PropertyDefinitionCount { get; init; }
    public int EventDefinitionCount { get; init; }
}

internal sealed class ArtifactManifestDocument
{
    public int SchemaVersion { get; init; }
    public string? ArtifactManifestDigest { get; init; }
    public List<ArtifactManifestEntryDocument?>? Entries { get; init; }
}

internal sealed class ArtifactManifestEntryDocument
{
    public string? RelativePath { get; init; }
    public ArtifactKind? Kind { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }
    public string? AssemblyName { get; init; }
    public string? ModuleName { get; init; }
    public int? TypeDefinitionCount { get; init; }
    public int? MethodDefinitionCount { get; init; }
    public int? FieldDefinitionCount { get; init; }
    public int? PropertyDefinitionCount { get; init; }
    public int? EventDefinitionCount { get; init; }
}

internal sealed class ValidationReportDocument
{
    public int SchemaVersion { get; init; }
    public string? AttemptId { get; init; }
    public ValidationSubjectKind? SubjectKind { get; init; }
    public string? SubjectExtractionId { get; init; }
    public string? BuildId { get; init; }
    public string? RecipeId { get; init; }
    public string? PolicyId { get; init; }
    public int PolicyVersion { get; init; }
    public string? PolicyDigest { get; init; }
    public ValidationOutcome? Outcome { get; init; }
    public bool? InputIntegrityPassed { get; init; }
    public bool? ProcessIntegrityPassed { get; init; }
    public bool? OutputContainmentPassed { get; init; }
    public string? ArtifactManifestDigest { get; init; }
    public StatisticsDocument? Statistics { get; init; }
    public string? BaselineExtractionId { get; init; }
    public List<ValidationMetricComparisonDocument?>? Comparisons { get; init; }
    public List<ValidationIssueDocument?>? Issues { get; init; }
    public bool? PreferenceEligible { get; init; }
    public DateTimeOffset? ValidatedAtUtc { get; init; }
}

internal sealed class ValidationMetricComparisonDocument
{
    public string? Metric { get; init; }
    public string? AssemblyName { get; init; }
    public long BaselineValue { get; init; }
    public long CandidateValue { get; init; }
    public double? RelativeChange { get; init; }
    public string? Classification { get; init; }
}

internal sealed class ValidationIssueDocument
{
    public ValidationIssueSeverity? Severity { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? ArtifactRelativePath { get; init; }
    public bool? PreferenceBlocking { get; init; }
}

internal sealed class CompleteMarkerDocument
{
    public int SchemaVersion { get; init; }
    public string? ExtractionId { get; init; }
    public string? ArtifactManifestDigest { get; init; }
    public string? ExtractionDocumentSha256 { get; init; }
    public string? ArtifactManifestDocumentSha256 { get; init; }
    public string? ValidationDocumentSha256 { get; init; }
}

// -- Resolved content (domain-shaped, produced only after strict validation) ----

/// <summary>
/// The content of <c>extraction.json</c>. Deliberately excludes the absolute
/// filesystem <c>RootPath</c> carried by <see cref="ValidatedExtraction"/>: no
/// immutable document may contain an absolute artifact path.
/// </summary>
internal sealed record ExtractionDocumentContent(
    int SchemaVersion,
    string ExtractionId,
    string RecipeId,
    string SourceAttemptId,
    string BuildId,
    string ToolInstanceId,
    string ProfileId,
    int ProfileVersion,
    string ProfileDigest,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    string ArtifactManifestDigest,
    DateTimeOffset CreatedAtUtc,
    ToolTrustLevel TrustLevel,
    ValidationOutcome InitialValidationOutcome,
    ExtractionStatistics Statistics);

/// <summary>The content of <c>artifact-manifest.json</c>: the manifest plus its self-declared digest field.</summary>
internal sealed record ArtifactManifestDocumentContent(
    ArtifactManifest Manifest,
    string DeclaredDigest);

/// <summary>The content of <c>complete.marker</c>.</summary>
internal sealed record CompleteMarkerContent(
    int SchemaVersion,
    string ExtractionId,
    string ArtifactManifestDigest,
    string ExtractionDocumentSha256,
    string ArtifactManifestDocumentSha256,
    string ValidationDocumentSha256);

/// <summary>The final documents root's file paths, all siblings directly under the extraction root.</summary>
internal sealed record ValidatedExtractionDocumentPaths(
    string ExtractionJsonPath,
    string ArtifactManifestJsonPath,
    string ValidationJsonPath,
    string CompleteMarkerPath)
{
    public static ValidatedExtractionDocumentPaths ForRoot(string documentsRoot) => new(
        Path.Combine(documentsRoot, "extraction.json"),
        Path.Combine(documentsRoot, "artifact-manifest.json"),
        Path.Combine(documentsRoot, "validation.json"),
        Path.Combine(documentsRoot, "complete.marker"));
}

/// <summary>All four strict documents, read and validated together.</summary>
internal sealed record ValidatedExtractionDocumentBundle(
    ExtractionDocumentContent Extraction,
    ArtifactManifestDocumentContent ArtifactManifest,
    ValidationReport ValidationReport,
    CompleteMarkerContent CompleteMarker,
    ValidatedExtractionDocumentPaths Paths);

/// <summary>Maps between the wire DTOs above and domain facts, validating content along the way.</summary>
internal static class ValidatedExtractionDocumentMapper
{
    private const string DigestParameterName = "digest";

    public static ExtractionDocument ToExtractionDocument(ValidatedExtraction extraction) => new()
    {
        SchemaVersion = ValidatedExtractionDocumentSchema.SchemaVersion,
        ExtractionId = extraction.ExtractionId,
        RecipeId = extraction.RecipeId,
        SourceAttemptId = extraction.SourceAttemptId,
        BuildId = extraction.BuildId,
        ToolInstanceId = extraction.ToolInstanceId,
        ProfileId = extraction.ProfileId,
        ProfileVersion = extraction.ProfileVersion,
        ProfileDigest = extraction.ProfileDigest,
        AdapterVersion = extraction.AdapterVersion,
        ExtractionSchemaVersion = extraction.ExtractionSchemaVersion,
        ArtifactManifestDigest = extraction.ArtifactManifestDigest,
        CreatedAtUtc = extraction.CreatedAtUtc,
        TrustLevel = extraction.TrustLevel,
        InitialValidationOutcome = extraction.InitialValidationOutcome,
        Statistics = ToStatisticsDocument(extraction.Statistics)
    };

    public static ExtractionDocumentContent? FromExtractionDocument(ExtractionDocument? document)
    {
        if (document is null || document.SchemaVersion != ValidatedExtractionDocumentSchema.SchemaVersion ||
            !HasText(document.ExtractionId) || !HasText(document.RecipeId) ||
            !HasText(document.SourceAttemptId) || !HasText(document.BuildId) ||
            !HasText(document.ToolInstanceId) || !HasText(document.ProfileId) ||
            document.ProfileVersion <= 0 || !HasText(document.ProfileDigest) ||
            document.AdapterVersion <= 0 || document.ExtractionSchemaVersion <= 0 ||
            !HasText(document.ArtifactManifestDigest) || document.CreatedAtUtc is null ||
            document.TrustLevel is null || document.InitialValidationOutcome is null ||
            FromStatisticsDocument(document.Statistics) is not { } statistics)
        {
            return null;
        }

        return new ExtractionDocumentContent(
            document.SchemaVersion, document.ExtractionId!, document.RecipeId!,
            document.SourceAttemptId!, document.BuildId!, document.ToolInstanceId!,
            document.ProfileId!, document.ProfileVersion, document.ProfileDigest!,
            document.AdapterVersion, document.ExtractionSchemaVersion,
            document.ArtifactManifestDigest!, document.CreatedAtUtc.Value,
            document.TrustLevel.Value, document.InitialValidationOutcome.Value, statistics);
    }

    public static ArtifactManifestDocument ToArtifactManifestDocument(
        ArtifactManifest manifest, string digest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest, DigestParameterName);

        var entries = manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(ToEntryDocument)
            .ToList();

        return new ArtifactManifestDocument
        {
            SchemaVersion = ValidatedExtractionDocumentSchema.SchemaVersion,
            ArtifactManifestDigest = digest,
            Entries = entries!
        };
    }

    public static ArtifactManifestDocumentContent? FromArtifactManifestDocument(ArtifactManifestDocument? document)
    {
        if (document is null || document.SchemaVersion != ValidatedExtractionDocumentSchema.SchemaVersion ||
            !HasText(document.ArtifactManifestDigest) || document.Entries is null)
        {
            return null;
        }

        var entries = new List<ArtifactManifestEntry>(document.Entries.Count);
        foreach (var entryDocument in document.Entries)
        {
            if (FromEntryDocument(entryDocument) is not { } entry)
            {
                return null;
            }

            entries.Add(entry);
        }

        var manifest = new ArtifactManifest(document.SchemaVersion, entries);
        return new ArtifactManifestDocumentContent(manifest, document.ArtifactManifestDigest!);
    }

    public static ValidationReportDocument ToValidationReportDocument(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ValidationReportDocument
        {
            SchemaVersion = report.SchemaVersion,
            AttemptId = report.AttemptId,
            SubjectKind = report.SubjectKind,
            SubjectExtractionId = report.SubjectExtractionId,
            BuildId = report.BuildId,
            RecipeId = report.RecipeId,
            PolicyId = report.PolicyId,
            PolicyVersion = report.PolicyVersion,
            PolicyDigest = report.PolicyDigest,
            Outcome = report.Outcome,
            InputIntegrityPassed = report.InputIntegrityPassed,
            ProcessIntegrityPassed = report.ProcessIntegrityPassed,
            OutputContainmentPassed = report.OutputContainmentPassed,
            ArtifactManifestDigest = report.ArtifactManifestDigest,
            Statistics = ToStatisticsDocument(report.Statistics),
            BaselineExtractionId = report.BaselineExtractionId,
            Comparisons = report.Comparisons.Select(ToComparisonDocument).ToList()!,
            Issues = report.Issues.Select(ToIssueDocument).ToList()!,
            PreferenceEligible = report.PreferenceEligible,
            ValidatedAtUtc = report.ValidatedAtUtc
        };
    }

    public static ValidationReport? FromValidationReportDocument(ValidationReportDocument? document)
    {
        if (document is null || document.SchemaVersion != ValidatedExtractionDocumentSchema.SchemaVersion ||
            !HasText(document.AttemptId) || document.SubjectKind is null ||
            !HasText(document.BuildId) || !HasText(document.RecipeId) ||
            !HasText(document.PolicyId) || document.PolicyVersion <= 0 ||
            !HasText(document.PolicyDigest) || document.Outcome is null ||
            document.InputIntegrityPassed is null || document.ProcessIntegrityPassed is null ||
            document.OutputContainmentPassed is null || !HasText(document.ArtifactManifestDigest) ||
            FromStatisticsDocument(document.Statistics) is not { } statistics ||
            document.Comparisons is null || document.Issues is null ||
            document.PreferenceEligible is null || document.ValidatedAtUtc is null)
        {
            return null;
        }

        var comparisons = new List<ValidationMetricComparison>(document.Comparisons.Count);
        foreach (var comparisonDocument in document.Comparisons)
        {
            if (FromComparisonDocument(comparisonDocument) is not { } comparison)
            {
                return null;
            }

            comparisons.Add(comparison);
        }

        var issues = new List<ValidationIssue>(document.Issues.Count);
        foreach (var issueDocument in document.Issues)
        {
            if (FromIssueDocument(issueDocument) is not { } issue)
            {
                return null;
            }

            issues.Add(issue);
        }

        return new ValidationReport(
            document.SchemaVersion, document.AttemptId!, document.SubjectKind.Value,
            document.SubjectExtractionId, document.BuildId!, document.RecipeId!,
            document.PolicyId!, document.PolicyVersion, document.PolicyDigest!,
            document.Outcome.Value, document.InputIntegrityPassed.Value,
            document.ProcessIntegrityPassed.Value, document.OutputContainmentPassed.Value,
            document.ArtifactManifestDigest!, statistics, document.BaselineExtractionId,
            comparisons, issues, document.PreferenceEligible.Value, document.ValidatedAtUtc.Value);
    }

    public static CompleteMarkerDocument ToCompleteMarkerDocument(CompleteMarkerContent marker) => new()
    {
        SchemaVersion = marker.SchemaVersion,
        ExtractionId = marker.ExtractionId,
        ArtifactManifestDigest = marker.ArtifactManifestDigest,
        ExtractionDocumentSha256 = marker.ExtractionDocumentSha256,
        ArtifactManifestDocumentSha256 = marker.ArtifactManifestDocumentSha256,
        ValidationDocumentSha256 = marker.ValidationDocumentSha256
    };

    public static CompleteMarkerContent? FromCompleteMarkerDocument(CompleteMarkerDocument? document)
    {
        if (document is null || document.SchemaVersion != ValidatedExtractionDocumentSchema.SchemaVersion ||
            !HasText(document.ExtractionId) || !HasText(document.ArtifactManifestDigest) ||
            !HasText(document.ExtractionDocumentSha256) ||
            !HasText(document.ArtifactManifestDocumentSha256) ||
            !HasText(document.ValidationDocumentSha256))
        {
            return null;
        }

        return new CompleteMarkerContent(
            document.SchemaVersion, document.ExtractionId!, document.ArtifactManifestDigest!,
            document.ExtractionDocumentSha256!, document.ArtifactManifestDocumentSha256!,
            document.ValidationDocumentSha256!);
    }

    private static StatisticsDocument ToStatisticsDocument(ExtractionStatistics statistics) => new()
    {
        ArtifactCount = statistics.ArtifactCount,
        LibraryCount = statistics.LibraryCount,
        ManagedAssemblyCount = statistics.ManagedAssemblyCount,
        TypeDefinitionCount = statistics.TypeDefinitionCount,
        MethodDefinitionCount = statistics.MethodDefinitionCount,
        FieldDefinitionCount = statistics.FieldDefinitionCount,
        PropertyDefinitionCount = statistics.PropertyDefinitionCount,
        EventDefinitionCount = statistics.EventDefinitionCount,
        TotalOutputBytes = statistics.TotalOutputBytes,
        TotalManagedBytes = statistics.TotalManagedBytes,
        Assemblies = statistics.Assemblies
            .OrderBy(assembly => assembly.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(assembly => assembly.AssemblyName, StringComparer.Ordinal)
            .Select(assembly => (AssemblyIdentityStatisticsDocument?)new AssemblyIdentityStatisticsDocument
            {
                AssemblyName = assembly.AssemblyName,
                FileCount = assembly.FileCount,
                ManagedBytes = assembly.ManagedBytes,
                TypeDefinitionCount = assembly.TypeDefinitionCount,
                MethodDefinitionCount = assembly.MethodDefinitionCount,
                FieldDefinitionCount = assembly.FieldDefinitionCount,
                PropertyDefinitionCount = assembly.PropertyDefinitionCount,
                EventDefinitionCount = assembly.EventDefinitionCount
            })
            .ToList()
    };

    private static ExtractionStatistics? FromStatisticsDocument(StatisticsDocument? document)
    {
        if (document is null || document.ArtifactCount < 0 || document.LibraryCount < 0 ||
            document.ManagedAssemblyCount < 0 || document.TypeDefinitionCount < 0 ||
            document.MethodDefinitionCount < 0 || document.FieldDefinitionCount < 0 ||
            document.PropertyDefinitionCount < 0 || document.EventDefinitionCount < 0 ||
            document.TotalOutputBytes < 0 || document.TotalManagedBytes < 0 ||
            document.Assemblies is null)
        {
            return null;
        }

        var assemblies = new List<AssemblyIdentityStatistics>(document.Assemblies.Count);
        foreach (var assemblyDocument in document.Assemblies)
        {
            if (assemblyDocument is null || !HasText(assemblyDocument.AssemblyName) ||
                assemblyDocument.FileCount < 0 || assemblyDocument.ManagedBytes < 0 ||
                assemblyDocument.TypeDefinitionCount < 0 || assemblyDocument.MethodDefinitionCount < 0 ||
                assemblyDocument.FieldDefinitionCount < 0 || assemblyDocument.PropertyDefinitionCount < 0 ||
                assemblyDocument.EventDefinitionCount < 0)
            {
                return null;
            }

            assemblies.Add(new AssemblyIdentityStatistics(
                assemblyDocument.AssemblyName!, assemblyDocument.FileCount, assemblyDocument.ManagedBytes,
                assemblyDocument.TypeDefinitionCount, assemblyDocument.MethodDefinitionCount,
                assemblyDocument.FieldDefinitionCount, assemblyDocument.PropertyDefinitionCount,
                assemblyDocument.EventDefinitionCount));
        }

        return new ExtractionStatistics(
            document.ArtifactCount, document.LibraryCount, document.ManagedAssemblyCount,
            document.TypeDefinitionCount, document.MethodDefinitionCount, document.FieldDefinitionCount,
            document.PropertyDefinitionCount, document.EventDefinitionCount, document.TotalOutputBytes,
            document.TotalManagedBytes, assemblies);
    }

    private static ArtifactManifestEntryDocument? ToEntryDocument(ArtifactManifestEntry entry) => new()
    {
        RelativePath = entry.RelativePath,
        Kind = entry.Kind,
        Size = entry.Size,
        Sha256 = entry.Sha256,
        AssemblyName = entry.AssemblyName,
        ModuleName = entry.ModuleName,
        TypeDefinitionCount = entry.TypeDefinitionCount,
        MethodDefinitionCount = entry.MethodDefinitionCount,
        FieldDefinitionCount = entry.FieldDefinitionCount,
        PropertyDefinitionCount = entry.PropertyDefinitionCount,
        EventDefinitionCount = entry.EventDefinitionCount
    };

    private static ArtifactManifestEntry? FromEntryDocument(ArtifactManifestEntryDocument? document)
    {
        if (document is null || !HasText(document.RelativePath) || document.Kind is null ||
            document.Size < 0 || !HasText(document.Sha256) ||
            document.TypeDefinitionCount is < 0 || document.MethodDefinitionCount is < 0 ||
            document.FieldDefinitionCount is < 0 || document.PropertyDefinitionCount is < 0 ||
            document.EventDefinitionCount is < 0)
        {
            return null;
        }

        return new ArtifactManifestEntry(
            document.RelativePath!, document.Kind.Value, document.Size, document.Sha256!,
            document.AssemblyName, document.ModuleName, document.TypeDefinitionCount,
            document.MethodDefinitionCount, document.FieldDefinitionCount,
            document.PropertyDefinitionCount, document.EventDefinitionCount);
    }

    private static ValidationMetricComparisonDocument? ToComparisonDocument(ValidationMetricComparison comparison) => new()
    {
        Metric = comparison.Metric,
        AssemblyName = comparison.AssemblyName,
        BaselineValue = comparison.BaselineValue,
        CandidateValue = comparison.CandidateValue,
        RelativeChange = comparison.RelativeChange,
        Classification = comparison.Classification
    };

    private static ValidationMetricComparison? FromComparisonDocument(ValidationMetricComparisonDocument? document)
    {
        if (document is null || !HasText(document.Metric) || !HasText(document.Classification))
        {
            return null;
        }

        return new ValidationMetricComparison(
            document.Metric!, document.AssemblyName, document.BaselineValue, document.CandidateValue,
            document.RelativeChange, document.Classification!);
    }

    private static ValidationIssueDocument? ToIssueDocument(ValidationIssue issue) => new()
    {
        Severity = issue.Severity,
        Code = issue.Code,
        Message = issue.Message,
        ArtifactRelativePath = issue.ArtifactRelativePath,
        PreferenceBlocking = issue.PreferenceBlocking
    };

    private static ValidationIssue? FromIssueDocument(ValidationIssueDocument? document)
    {
        if (document is null || document.Severity is null || !HasText(document.Code) ||
            !HasText(document.Message) || document.PreferenceBlocking is null)
        {
            return null;
        }

        return new ValidationIssue(
            document.Severity.Value, document.Code!, document.Message!,
            document.ArtifactRelativePath, document.PreferenceBlocking.Value);
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}
