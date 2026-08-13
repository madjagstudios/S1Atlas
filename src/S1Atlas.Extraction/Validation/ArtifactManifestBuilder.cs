using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// The outcome of <see cref="ArtifactManifestBuilder.Build"/>: an annotated,
/// deterministic <see cref="ArtifactManifest"/>, its content digest (unchanged
/// when only annotations differ, per <see cref="ArtifactManifestFingerprint"/>),
/// aggregate <see cref="ExtractionStatistics"/> grouped by assembly identity, and
/// any structural issues discovered while annotating artifacts (invalid managed
/// DLLs, duplicate assembly identities).
/// </summary>
internal sealed record ArtifactBuildResult(
    ArtifactManifest Manifest,
    string ManifestDigest,
    ExtractionStatistics Statistics,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Annotates every artifact in a contained <see cref="CandidateInventory"/> with
/// its <see cref="ArtifactKind"/> and, for managed assemblies, exact metadata
/// table counts, using <see cref="ManagedAssemblyInspector"/>. Never rewrites
/// candidate bytes: identical duplicate artifacts are retained verbatim in the
/// manifest. Aggregates statistics by assembly identity, grouped ordinal-ignore-
/// case, so a validator can compare production Cpp2IL casing drift without
/// treating it as a distinct identity.
/// </summary>
internal static class ArtifactManifestBuilder
{
    private const int SchemaVersion = 1;
    private const string InvalidManagedAssemblyCode = "InvalidManagedAssembly";
    private const string DuplicateAssemblyIdentityCode = "DuplicateAssemblyIdentity";
    private const string DllExtension = ".dll";

    public static ArtifactBuildResult Build(CandidateInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var issues = new List<ValidationIssue>();
        var entries = new List<ArtifactManifestEntry>(inventory.Artifacts.Count);

        foreach (var artifact in inventory.Artifacts)
        {
            var inspection = ManagedAssemblyInspector.Inspect(artifact.FullPath, artifact.RelativePath);

            if (!inspection.IsValid)
            {
                issues.Add(new ValidationIssue(
                    ValidationIssueSeverity.Error,
                    InvalidManagedAssemblyCode,
                    inspection.FailureMessage ??
                        $"'{artifact.RelativePath}' is not a valid managed assembly.",
                    artifact.RelativePath,
                    PreferenceBlocking: true));
            }

            entries.Add(new ArtifactManifestEntry(
                artifact.RelativePath,
                inspection.Kind,
                artifact.Size,
                artifact.Sha256,
                inspection.AssemblyName,
                inspection.ModuleName,
                inspection.TypeDefinitionCount,
                inspection.MethodDefinitionCount,
                inspection.FieldDefinitionCount,
                inspection.PropertyDefinitionCount,
                inspection.EventDefinitionCount));
        }

        var manifest = new ArtifactManifest(SchemaVersion, entries);
        var manifestDigest = ArtifactManifestFingerprint.Create(manifest);

        var assemblyGroups = entries
            .Where(entry => entry.Kind == ArtifactKind.ManagedAssembly && entry.AssemblyName is not null)
            .GroupBy(entry => entry.AssemblyName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        var assemblyStatistics = new List<AssemblyIdentityStatistics>();
        foreach (var group in assemblyGroups)
        {
            var groupEntries = group
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();

            if (groupEntries.Length > 1)
            {
                var distinctHashCount = groupEntries
                    .Select(entry => entry.Sha256)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                issues.Add(distinctHashCount > 1
                    ? new ValidationIssue(
                        ValidationIssueSeverity.Error,
                        DuplicateAssemblyIdentityCode,
                        $"Assembly identity '{group.Key}' appears {groupEntries.Length} times " +
                            "with different content.",
                        ArtifactRelativePath: null,
                        PreferenceBlocking: true)
                    : new ValidationIssue(
                        ValidationIssueSeverity.Information,
                        DuplicateAssemblyIdentityCode,
                        $"Assembly identity '{group.Key}' appears {groupEntries.Length} times " +
                            "with identical content.",
                        ArtifactRelativePath: null,
                        PreferenceBlocking: false));
            }

            assemblyStatistics.Add(new AssemblyIdentityStatistics(
                group.Key,
                groupEntries.Length,
                groupEntries.Sum(entry => entry.Size),
                groupEntries.Sum(entry => entry.TypeDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.MethodDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.FieldDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.PropertyDefinitionCount ?? 0),
                groupEntries.Sum(entry => entry.EventDefinitionCount ?? 0)));
        }

        var statistics = new ExtractionStatistics(
            ArtifactCount: entries.Count,
            LibraryCount: entries.Count(entry =>
                entry.RelativePath.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase)),
            ManagedAssemblyCount: entries.Count(entry => entry.Kind == ArtifactKind.ManagedAssembly),
            TypeDefinitionCount: assemblyStatistics.Sum(assembly => assembly.TypeDefinitionCount),
            MethodDefinitionCount: assemblyStatistics.Sum(assembly => assembly.MethodDefinitionCount),
            FieldDefinitionCount: assemblyStatistics.Sum(assembly => assembly.FieldDefinitionCount),
            PropertyDefinitionCount: assemblyStatistics.Sum(assembly => assembly.PropertyDefinitionCount),
            EventDefinitionCount: assemblyStatistics.Sum(assembly => assembly.EventDefinitionCount),
            TotalOutputBytes: entries.Sum(entry => entry.Size),
            TotalManagedBytes: entries
                .Where(entry => entry.Kind == ArtifactKind.ManagedAssembly)
                .Sum(entry => entry.Size),
            Assemblies: assemblyStatistics);

        return new ArtifactBuildResult(manifest, manifestDigest, statistics, issues);
    }
}
