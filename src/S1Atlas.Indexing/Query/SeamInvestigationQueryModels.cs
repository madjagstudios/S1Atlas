using System.Text.Json.Serialization;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed record SeamInvestigationRequest(
    string BehavioralQuestion,
    string Selector,
    IndexQueryOptions Options,
    int RelationshipLimit = 50,
    int OwnerCandidateLimit = 10,
    int SourceContext = 5,
    bool IncludeDetails = false,
    IReadOnlyList<string>? NativeSymbolIds = null,
    int NativeTraversalBudget = 0)
{
    private string _behavioralQuestion = RequireNonBlank(BehavioralQuestion, nameof(BehavioralQuestion));
    private string _selector = RequireNonBlank(Selector, nameof(Selector));
    private IndexQueryOptions _options = RequireOptions(Options);
    private int _relationshipLimit = RequireBoundedPositive(RelationshipLimit, 1, 50, nameof(RelationshipLimit));
    private int _ownerCandidateLimit = RequireBoundedPositive(OwnerCandidateLimit, 1, 50, nameof(OwnerCandidateLimit));
    private int _sourceContext = RequireNonnegative(SourceContext, nameof(SourceContext));
    private IReadOnlyList<string>? _nativeSymbolIds = NormalizeNativeSymbolIds(NativeSymbolIds);
    private int _nativeTraversalBudget = RequireBoundedNonnegative(NativeTraversalBudget, 500, nameof(NativeTraversalBudget));

    public string BehavioralQuestion
    {
        get => _behavioralQuestion;
        init => _behavioralQuestion = RequireNonBlank(value, nameof(BehavioralQuestion));
    }

    public string Selector
    {
        get => _selector;
        init => _selector = RequireNonBlank(value, nameof(Selector));
    }

    public IndexQueryOptions Options
    {
        get => _options;
        init => _options = RequireOptions(value);
    }

    public int RelationshipLimit
    {
        get => _relationshipLimit;
        init => _relationshipLimit = RequireBoundedPositive(value, 1, 50, nameof(RelationshipLimit));
    }

    public int OwnerCandidateLimit
    {
        get => _ownerCandidateLimit;
        init => _ownerCandidateLimit = RequireBoundedPositive(value, 1, 50, nameof(OwnerCandidateLimit));
    }

    public int SourceContext
    {
        get => _sourceContext;
        init => _sourceContext = RequireNonnegative(value, nameof(SourceContext));
    }

    public IReadOnlyList<string>? NativeSymbolIds
    {
        get => _nativeSymbolIds;
        init => _nativeSymbolIds = NormalizeNativeSymbolIds(value);
    }

    public int NativeTraversalBudget
    {
        get => _nativeTraversalBudget;
        init => _nativeTraversalBudget = RequireBoundedNonnegative(value, 500, nameof(NativeTraversalBudget));
    }

    private static string RequireNonBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static IndexQueryOptions RequireOptions(IndexQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options;
    }

    private static int RequireBoundedPositive(int value, int minimum, int maximum, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, minimum, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum, parameterName);
        return value;
    }

    private static int RequireNonnegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static int RequireBoundedNonnegative(int value, int maximum, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum, parameterName);
        return value;
    }

    private static IReadOnlyList<string>? NormalizeNativeSymbolIds(IReadOnlyList<string>? values)
    {
        if (values is null)
            return null;

        var normalized = values
            .Select(value => RequireNonBlank(value, nameof(NativeSymbolIds)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length != values.Count)
            throw new ArgumentException("Native symbol IDs must be unique.", nameof(NativeSymbolIds));
        return normalized;
    }
}

public enum SeamConclusion
{
    SupportableSeam,
    NoSupportableSeam,
    InsufficientCoverage
}

public enum EvidenceCoverage
{
    Complete,
    Bounded,
    Incomplete,
    Unavailable,
    NotApplicable
}

public enum SeamEvidenceClassification
{
    Fact,
    Derived,
    Unknown
}

public sealed record SeamEvidenceClaim
{
    private string _dimension = null!;
    private string _classification = null!;
    private string _statement = null!;
    private IReadOnlyList<string> _evidenceIds = null!;

    public SeamEvidenceClaim(
        string dimension,
        string classification,
        string statement,
        IReadOnlyList<string> evidenceIds)
    {
        Dimension = dimension;
        Classification = classification;
        Statement = statement;
        EvidenceIds = evidenceIds;
    }

    public SeamEvidenceClaim(
        string dimension,
        SeamEvidenceClassification classification,
        string statement,
        IReadOnlyList<string> evidenceIds)
        : this(dimension, ToWireValue(classification), statement, evidenceIds)
    {
    }

    public string Dimension
    {
        get => _dimension;
        init => _dimension = RequireNonBlank(value, nameof(Dimension));
    }

    // Retained as the serialized compatibility surface; EvidenceClassification is the closed model value.
    public string Classification
    {
        get => _classification;
        init => _classification = ToWireValue(ParseClassification(value, nameof(Classification)));
    }

    public SeamEvidenceClassification EvidenceClassification => ParseClassification(Classification, nameof(Classification));

    public string Statement
    {
        get => _statement;
        init => _statement = RequireNonBlank(value, nameof(Statement));
    }

    public IReadOnlyList<string> EvidenceIds
    {
        get => _evidenceIds;
        init => _evidenceIds = value ?? throw new ArgumentNullException(nameof(EvidenceIds));
    }

    private static string RequireNonBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static SeamEvidenceClassification ParseClassification(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim().ToUpperInvariant() switch
        {
            "FACT" => SeamEvidenceClassification.Fact,
            "DERIVED" => SeamEvidenceClassification.Derived,
            "UNKNOWN" => SeamEvidenceClassification.Unknown,
            _ => throw new ArgumentException(
                "Evidence classification must be FACT, DERIVED, or UNKNOWN.",
                parameterName)
        };
    }

    private static string ToWireValue(SeamEvidenceClassification classification) =>
        classification switch
        {
            SeamEvidenceClassification.Fact => "FACT",
            SeamEvidenceClassification.Derived => "DERIVED",
            SeamEvidenceClassification.Unknown => "UNKNOWN",
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown evidence classification.")
        };
}

public sealed record SeamEvidencePath(
    IReadOnlyList<string> RelationshipIds,
    int PathLength,
    int SupportingRelationshipFamilyCount);

public sealed record SeamOwnerCandidate(
    SymbolQueryResult Symbol,
    string Role,
    SeamEvidencePath Path,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamEvidenceSection(
    string Family,
    EvidenceCoverage Coverage,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<string> EvidenceIds,
    string? Notice);

public sealed record SeamNextAction(
    string Kind,
    string Reason,
    string Scope,
    bool RequiresRuntimeProof);

public sealed record SeamPinnedProvenance(
    string? RequestedBuildId,
    string? ResolvedBuildId,
    string? ExtractionId,
    string? IndexId,
    string? Codebase,
    string? Channel,
    bool IntegrityVerified = false);

public sealed record SeamAuthorityEntityAttribution(
    string Authority,
    string Entity,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamAlternateGenericCallerEvidence(
    IReadOnlyList<SeamOwnerCandidate> Callers,
    bool IsExclusive,
    EvidenceCoverage Coverage,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamLifecyclePositionAndBeforeAfterState(
    string Position,
    string BeforeState,
    string AfterState,
    EvidenceCoverage Coverage,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamApiBeforePatchResult(
    string ApiSurface,
    string Result,
    EvidenceCoverage Coverage,
    IReadOnlyList<string> EvidenceIds);

public sealed record NativeEvidenceSummary(
    NativeRecoveryStatus Status,
    NativeEvidenceLookupStatus LookupStatus,
    bool IsComplete,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeEvidenceEdge> DirectEdges,
    IReadOnlyList<string> FieldAccesses,
    string ToolProvenance,
    string OutputSha256,
    string? FailureMessage = null);

public enum NativeEvidenceLookupStatus
{
    Matched,
    NoMatch,
    InputChanged,
    Unavailable
}

public sealed record SeamInvestigationResult(
    string BehavioralQuestion,
    SeamConclusion Conclusion,
    SymbolResolutionResult Resolution,
    SymbolQueryResult? Candidate,
    string CandidateRole,
    BodyRecoveryStatus? BodyRecoveryStatus,
    EvidenceCoverage BodyCoverage,
    EvidenceCoverage CallableCoverage,
    IReadOnlyList<SeamEvidenceClaim> Claims,
    IReadOnlyList<SeamEvidenceSection> EvidenceSections,
    IReadOnlyList<SeamOwnerCandidate> OwnerCandidates,
    IReadOnlyList<string> CoverageWarnings,
    IReadOnlyList<string> UnknownDimensions,
    IReadOnlyList<SeamNextAction> NextActions,
    SeamPinnedProvenance? PinnedProvenance = null,
    SeamAuthorityEntityAttribution? AuthorityEntityAttribution = null,
    SeamAlternateGenericCallerEvidence? AlternateGenericCallersAndExclusivity = null,
    SeamLifecyclePositionAndBeforeAfterState? LifecyclePositionAndBeforeAfterState = null,
    SeamApiBeforePatchResult? ApiBeforePatchResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeEvidenceSummary? NativeEvidence = null)
{
    public SeamInvestigationResult ProjectDetails(bool includeDetails) =>
        includeDetails
            ? this
            : this with
            {
                Claims = Array.Empty<SeamEvidenceClaim>(),
                EvidenceSections = Array.Empty<SeamEvidenceSection>()
            };
}
