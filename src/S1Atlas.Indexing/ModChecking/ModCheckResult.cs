namespace S1Atlas.Indexing.ModChecking;

public sealed record ModDependencySource(string Kind, string? PatchKind, string Member, string Evidence);
public sealed record ModReplacement(string CanonicalKey, string Name, string Signature, string Evidence);
public sealed record ModDependency(
    string? CanonicalKey, string Name, string Kind, string Status,
    string? BeforeSignature, string? AfterSignature, bool? BodyChanged,
    IReadOnlyList<ModDependencySource> Sources, IReadOnlyList<ModReplacement> Candidates,
    string? Reason, string Evidence);
public sealed record ModCheckSummary(
    IReadOnlyDictionary<string, int> Counts, IReadOnlyDictionary<string, int> PatchTargets,
    int ExternalReferencesNotChecked, int BreakingDependencies, int BreakingPatchTargets);
public sealed record ModCheckResult(
    string? FromBuildId, string ToBuildId, string? FromIndexId, string ToIndexId, bool SingleBuild,
    ModCheckSummary Summary, IReadOnlyList<ModDependency> Dependencies);
