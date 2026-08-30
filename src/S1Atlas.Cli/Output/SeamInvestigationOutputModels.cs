using System.Text.Json.Serialization;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Output;

internal sealed record SeamInvestigationOutput(
    string BehavioralQuestion,
    string Conclusion,
    SeamResolutionOutput Resolution,
    SymbolQueryResult? Candidate,
    string CandidateRole,
    string? BodyRecoveryStatus,
    string BodyCoverage,
    string CallableCoverage,
    IReadOnlyList<SeamEvidenceClaimOutput> Claims,
    IReadOnlyList<SeamEvidenceSectionOutput> EvidenceSections,
    IReadOnlyList<SeamOwnerCandidateOutput> OwnerCandidates,
    IReadOnlyList<string> CoverageWarnings,
    IReadOnlyList<string> UnknownDimensions,
    IReadOnlyList<SeamNextActionOutput> NextActions,
    SeamPinnedProvenanceOutput PinnedProvenance,
    SeamAuthorityEntityAttributionOutput AuthorityEntityAttribution,
    SeamAlternateGenericCallerEvidenceOutput AlternateGenericCallersAndExclusivity,
    SeamLifecyclePositionAndBeforeAfterStateOutput LifecyclePositionAndBeforeAfterState,
    SeamApiBeforePatchResultOutput ApiBeforePatchResult,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeEvidenceSummaryOutput? NativeEvidence = null,
    SeamPinnedProvenanceOutput? ReferenceCollectionBaseProvenance = null)
{
    public static SeamInvestigationOutput FromResult(
        SeamInvestigationResult result,
        SeamPinnedProvenanceOutput? referenceCollectionBaseProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SeamInvestigationOutput(
            result.BehavioralQuestion,
            result.Conclusion.ToString(),
            SeamResolutionOutput.FromResult(result.Resolution),
            result.Candidate,
            result.CandidateRole,
            result.BodyRecoveryStatus?.ToString(),
            result.BodyCoverage.ToString(),
            result.CallableCoverage.ToString(),
            result.Claims.Select(SeamEvidenceClaimOutput.FromResult).ToArray(),
            result.EvidenceSections.Select(SeamEvidenceSectionOutput.FromResult).ToArray(),
            result.OwnerCandidates.Select(SeamOwnerCandidateOutput.FromResult).ToArray(),
            result.CoverageWarnings.ToArray(),
            result.UnknownDimensions.ToArray(),
            result.NextActions.Select(SeamNextActionOutput.FromResult).ToArray(),
            SeamPinnedProvenanceOutput.FromResult(result.PinnedProvenance),
            SeamAuthorityEntityAttributionOutput.FromResult(result.AuthorityEntityAttribution),
            SeamAlternateGenericCallerEvidenceOutput.FromResult(result.AlternateGenericCallersAndExclusivity),
            SeamLifecyclePositionAndBeforeAfterStateOutput.FromResult(result.LifecyclePositionAndBeforeAfterState),
            SeamApiBeforePatchResultOutput.FromResult(result.ApiBeforePatchResult),
            NativeEvidenceSummaryOutput.FromResult(result.NativeEvidence),
            referenceCollectionBaseProvenance);
    }
}

internal sealed record SeamResolutionOutput(
    string Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates)
{
    public static SeamResolutionOutput FromResult(SymbolResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SeamResolutionOutput(
            result.Status.ToString(),
            result.Symbol,
            result.Candidates.ToArray());
    }
}

internal sealed record SeamEvidenceClaimOutput(
    string Dimension,
    string Classification,
    string Statement,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamEvidenceClaimOutput FromResult(SeamEvidenceClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new SeamEvidenceClaimOutput(
            claim.Dimension,
            claim.Classification,
            claim.Statement,
            claim.EvidenceIds.ToArray());
    }
}

internal sealed record SeamEvidencePathOutput(
    IReadOnlyList<string> RelationshipIds,
    int PathLength,
    int SupportingRelationshipFamilyCount)
{
    public static SeamEvidencePathOutput FromResult(SeamEvidencePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new SeamEvidencePathOutput(
            path.RelationshipIds.ToArray(),
            path.PathLength,
            path.SupportingRelationshipFamilyCount);
    }
}

internal sealed record SeamOwnerCandidateOutput(
    SymbolQueryResult Symbol,
    string Role,
    SeamEvidencePathOutput Path,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamOwnerCandidateOutput FromResult(SeamOwnerCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new SeamOwnerCandidateOutput(
            candidate.Symbol,
            candidate.Role,
            SeamEvidencePathOutput.FromResult(candidate.Path),
            candidate.EvidenceIds.ToArray());
    }
}

internal sealed record SeamEvidenceSectionOutput(
    string Family,
    string Coverage,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<string> EvidenceIds,
    string? Notice)
{
    public static SeamEvidenceSectionOutput FromResult(SeamEvidenceSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new SeamEvidenceSectionOutput(
            section.Family,
            section.Coverage.ToString(),
            section.TotalCount,
            section.ReturnedCount,
            section.EvidenceIds.ToArray(),
            section.Notice);
    }
}

internal sealed record SeamNextActionOutput(
    string Kind,
    string Reason,
    string Scope,
    bool RequiresRuntimeProof)
{
    public static SeamNextActionOutput FromResult(SeamNextAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new SeamNextActionOutput(
            action.Kind,
            action.Reason,
            action.Scope,
            action.RequiresRuntimeProof);
    }
}

internal sealed record SeamPinnedProvenanceOutput(
    string? RequestedBuildId,
    string? ResolvedBuildId,
    string? ExtractionId,
    string? IndexId,
    string Codebase,
    string Channel,
    bool IntegrityVerified)
{
    public static SeamPinnedProvenanceOutput FromResult(SeamPinnedProvenance? pinnedProvenance) =>
        new(
            pinnedProvenance?.RequestedBuildId,
            pinnedProvenance?.ResolvedBuildId,
            pinnedProvenance?.ExtractionId,
            pinnedProvenance?.IndexId,
            pinnedProvenance?.Codebase ?? string.Empty,
            pinnedProvenance?.Channel ?? string.Empty,
            pinnedProvenance?.IntegrityVerified ?? false);
}

internal sealed record SeamAuthorityEntityAttributionOutput(
    string Authority,
    string Entity,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamAuthorityEntityAttributionOutput FromResult(SeamAuthorityEntityAttribution? attribution) =>
        new(
            attribution?.Authority ?? string.Empty,
            attribution?.Entity ?? string.Empty,
            attribution?.EvidenceIds.ToArray() ?? []);
}

internal sealed record SeamAlternateGenericCallerEvidenceOutput(
    IReadOnlyList<SeamOwnerCandidateOutput> Callers,
    bool IsExclusive,
    string Coverage,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamAlternateGenericCallerEvidenceOutput FromResult(SeamAlternateGenericCallerEvidence? callersAndExclusivity) =>
        new(
            callersAndExclusivity?.Callers.Select(SeamOwnerCandidateOutput.FromResult).ToArray() ?? [],
            callersAndExclusivity?.IsExclusive ?? false,
            callersAndExclusivity?.Coverage.ToString() ?? EvidenceCoverage.Unavailable.ToString(),
            callersAndExclusivity?.EvidenceIds.ToArray() ?? []);
}

internal sealed record SeamLifecyclePositionAndBeforeAfterStateOutput(
    string Position,
    string BeforeState,
    string AfterState,
    string Coverage,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamLifecyclePositionAndBeforeAfterStateOutput FromResult(
        SeamLifecyclePositionAndBeforeAfterState? lifecycle) =>
        new(
            lifecycle?.Position ?? string.Empty,
            lifecycle?.BeforeState ?? string.Empty,
            lifecycle?.AfterState ?? string.Empty,
            lifecycle?.Coverage.ToString() ?? EvidenceCoverage.Unavailable.ToString(),
            lifecycle?.EvidenceIds.ToArray() ?? []);
}

internal sealed record SeamApiBeforePatchResultOutput(
    string ApiSurface,
    string Result,
    string Coverage,
    IReadOnlyList<string> EvidenceIds)
{
    public static SeamApiBeforePatchResultOutput FromResult(SeamApiBeforePatchResult? apiBeforePatch) =>
        new(
            apiBeforePatch?.ApiSurface ?? string.Empty,
            apiBeforePatch?.Result ?? string.Empty,
            apiBeforePatch?.Coverage.ToString() ?? EvidenceCoverage.Unavailable.ToString(),
            apiBeforePatch?.EvidenceIds.ToArray() ?? []);
}

internal sealed record NativeEvidenceSummaryOutput(
    string Status,
    string LookupStatus,
    bool IsComplete,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeEvidenceEdge> DirectEdges,
    IReadOnlyList<string> FieldAccesses,
    string ToolProvenance,
    string OutputSha256,
    string? FailureMessage)
{
    public static NativeEvidenceSummaryOutput? FromResult(NativeEvidenceSummary? nativeEvidence) =>
        nativeEvidence is null
            ? null
            : new(
                nativeEvidence.Status.ToString(),
                nativeEvidence.LookupStatus.ToString(),
                nativeEvidence.IsComplete,
                nativeEvidence.MappingEvidence.ToArray(),
                nativeEvidence.DirectEdges.ToArray(),
                nativeEvidence.FieldAccesses.ToArray(),
                nativeEvidence.ToolProvenance,
                nativeEvidence.OutputSha256,
                nativeEvidence.FailureMessage);
}
