using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// A single regular file discovered inside an owned Phase 3 candidate output tree,
/// hashed exactly once and observed to be stable while it was hashed.
/// <see cref="RelativePath"/> is normalized with '/' and begins with
/// "reconstructed/", matching <see cref="ArtifactManifestEntry.RelativePath"/>.
/// </summary>
internal sealed record CandidateArtifact(
    string FullPath,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>
/// The complete, deterministically ordered inventory of a contained candidate
/// output tree. <see cref="Artifacts"/> is sorted ordinally by
/// <see cref="CandidateArtifact.RelativePath"/>.
/// </summary>
internal sealed record CandidateInventory(
    string CandidateRoot,
    IReadOnlyList<CandidateArtifact> Artifacts,
    long TotalBytes);

/// <summary>
/// The outcome of <see cref="CandidateOutputInspector"/>. A containment, race,
/// unreadable, or path-collision defect returns <see cref="Inventory"/> as
/// <see langword="null"/>, <see cref="ContainmentPassed"/> as
/// <see langword="false"/>, and a single <see cref="ValidationIssue"/> describing
/// the defect, so a caller can still write a strict validation.json and fail the
/// attempt instead of bypassing the validation record through a generic exception.
/// </summary>
internal sealed record CandidateInspectionResult(
    CandidateInventory? Inventory,
    bool ContainmentPassed,
    IReadOnlyList<ValidationIssue> Issues);
