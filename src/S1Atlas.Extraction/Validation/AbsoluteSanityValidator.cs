using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// Applies the committed <see cref="ValidationPolicy"/>'s absolute (non-
/// comparative) sanity checks to a built <see cref="ArtifactBuildResult"/> and
/// returns a single, deterministically ordered issue list: structural defects
/// first (no/empty artifacts, no managed assemblies, a missing required
/// identity), then the annotation issues <see cref="ArtifactManifestBuilder"/>
/// already found (invalid managed DLLs, duplicate assembly identities), then the
/// policy's minimum managed-assembly/type/method/byte thresholds. Comparative
/// (preferred-baseline) and reproducibility (same-recipe) checks are Task 5's
/// concern and are not evaluated here.
/// </summary>
internal static class AbsoluteSanityValidator
{
    private const string NoArtifactsProducedCode = "NoArtifactsProduced";
    private const string EmptyArtifactCode = "EmptyArtifact";
    private const string NoManagedAssembliesProducedCode = "NoManagedAssembliesProduced";
    private const string RequiredAssemblyMissingCode = "RequiredAssemblyMissing";
    private const string InvalidManagedAssemblyCode = "InvalidManagedAssembly";
    private const string DuplicateAssemblyIdentityCode = "DuplicateAssemblyIdentity";
    private const string InsufficientManagedAssemblyCountCode = "InsufficientManagedAssemblyCount";
    private const string InsufficientTypeDefinitionCountCode = "InsufficientTypeDefinitionCount";
    private const string InsufficientMethodDefinitionCountCode = "InsufficientMethodDefinitionCount";
    private const string InsufficientManagedBytesCode = "InsufficientManagedBytes";

    public static IReadOnlyList<ValidationIssue> Validate(
        ArtifactBuildResult artifactBuild,
        ResolvedValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(artifactBuild);
        ArgumentNullException.ThrowIfNull(policy);

        var manifest = artifactBuild.Manifest;
        var statistics = artifactBuild.Statistics;
        var definition = policy.Policy;
        var issues = new List<ValidationIssue>();

        if (statistics.ArtifactCount == 0)
        {
            issues.Add(Issue(
                ValidationIssueSeverity.Error,
                NoArtifactsProducedCode,
                "The candidate output produced no artifacts.",
                artifactRelativePath: null));
        }

        foreach (var entry in manifest.Entries)
        {
            if (entry.Size == 0)
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    EmptyArtifactCode,
                    $"Artifact '{entry.RelativePath}' is empty.",
                    entry.RelativePath));
            }
        }

        if (statistics.ArtifactCount > 0 && statistics.ManagedAssemblyCount == 0)
        {
            issues.Add(Issue(
                ValidationIssueSeverity.Error,
                NoManagedAssembliesProducedCode,
                "The candidate output produced no managed assemblies.",
                artifactRelativePath: null));
        }

        var presentIdentities = new HashSet<string>(
            statistics.Assemblies.Select(assembly => assembly.AssemblyName),
            StringComparer.OrdinalIgnoreCase);
        foreach (var required in definition.RequiredAssemblyIdentities)
        {
            if (!presentIdentities.Contains(required))
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    RequiredAssemblyMissingCode,
                    $"Required assembly identity '{required}' is missing from the candidate output.",
                    artifactRelativePath: null));
            }
        }

        issues.AddRange(artifactBuild.Issues.Where(issue => issue.Code == InvalidManagedAssemblyCode));
        issues.AddRange(artifactBuild.Issues.Where(issue => issue.Code == DuplicateAssemblyIdentityCode));

        if (statistics.ManagedAssemblyCount > 0)
        {
            if (statistics.ManagedAssemblyCount < definition.MinimumManagedAssemblyCount)
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    InsufficientManagedAssemblyCountCode,
                    $"The candidate output produced {statistics.ManagedAssemblyCount} managed " +
                        $"assembly file(s); the policy requires at least " +
                        $"{definition.MinimumManagedAssemblyCount}.",
                    artifactRelativePath: null));
            }

            if (statistics.TypeDefinitionCount < definition.MinimumTypeDefinitionCount)
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    InsufficientTypeDefinitionCountCode,
                    $"The candidate output produced {statistics.TypeDefinitionCount} managed " +
                        $"type definition(s); the policy requires at least " +
                        $"{definition.MinimumTypeDefinitionCount}.",
                    artifactRelativePath: null));
            }

            if (statistics.MethodDefinitionCount < definition.MinimumMethodDefinitionCount)
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    InsufficientMethodDefinitionCountCode,
                    $"The candidate output produced {statistics.MethodDefinitionCount} managed " +
                        $"method definition(s); the policy requires at least " +
                        $"{definition.MinimumMethodDefinitionCount}.",
                    artifactRelativePath: null));
            }

            if (statistics.TotalManagedBytes < definition.MinimumTotalManagedBytes)
            {
                issues.Add(Issue(
                    ValidationIssueSeverity.Error,
                    InsufficientManagedBytesCode,
                    $"The candidate output produced {statistics.TotalManagedBytes} managed " +
                        $"byte(s); the policy requires at least {definition.MinimumTotalManagedBytes}.",
                    artifactRelativePath: null));
            }
        }

        return issues;
    }

    private static ValidationIssue Issue(
        ValidationIssueSeverity severity,
        string code,
        string message,
        string? artifactRelativePath) =>
        new(severity, code, message, artifactRelativePath, PreferenceBlocking: severity == ValidationIssueSeverity.Error);
}
