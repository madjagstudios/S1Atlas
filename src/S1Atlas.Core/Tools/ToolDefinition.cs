namespace S1Atlas.Core.Tools;

public sealed record ToolSafetyLimits(
    long MaximumDownloadBytes,
    long MaximumExpandedBytes,
    int MaximumFileCount);

public sealed record ToolPackageDefinition(
    ToolPackageKind Kind,
    ToolArchiveFormat? ArchiveFormat,
    Uri SourceUri,
    Uri ReleaseUri,
    string AssetName,
    long ExpectedSize,
    string Sha256,
    string ExecutableRelativePath,
    ToolSafetyLimits Limits);

public sealed record ToolLicenseDefinition(
    string SpdxIdentifier,
    Uri SourceUri);

public sealed record ToolProbeDefinition(
    string ProbeId,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int> AcceptedExitCodes,
    TimeSpan Timeout,
    IReadOnlyList<string> RequiredOutputSubstrings);

public sealed record ToolDefinition(
    int SchemaVersion,
    string ToolId,
    string DisplayName,
    string Version,
    string Platform,
    ToolPackageDefinition Package,
    ToolLicenseDefinition License,
    IReadOnlyList<ToolProbeDefinition> Probes)
{
    public bool Equals(ToolDefinition? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null &&
            SchemaVersion == other.SchemaVersion &&
            string.Equals(ToolId, other.ToolId, StringComparison.Ordinal) &&
            string.Equals(
                DisplayName,
                other.DisplayName,
                StringComparison.Ordinal) &&
            string.Equals(Version, other.Version, StringComparison.Ordinal) &&
            string.Equals(Platform, other.Platform, StringComparison.Ordinal) &&
            Package == other.Package &&
            License == other.License &&
            Probes.Count == other.Probes.Count &&
            Probes.Zip(other.Probes).All(pair =>
                ProbeEquals(pair.First, pair.Second));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(ToolId, StringComparer.Ordinal);
        hash.Add(DisplayName, StringComparer.Ordinal);
        hash.Add(Version, StringComparer.Ordinal);
        hash.Add(Platform, StringComparer.Ordinal);
        hash.Add(Package);
        hash.Add(License);
        foreach (var probe in Probes)
        {
            hash.Add(probe.ProbeId, StringComparer.Ordinal);
            foreach (var argument in probe.Arguments)
            {
                hash.Add(argument, StringComparer.Ordinal);
            }

            foreach (var exitCode in probe.AcceptedExitCodes)
            {
                hash.Add(exitCode);
            }

            hash.Add(probe.Timeout);
            foreach (var requiredOutput in probe.RequiredOutputSubstrings)
            {
                hash.Add(requiredOutput, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    private static bool ProbeEquals(
        ToolProbeDefinition left,
        ToolProbeDefinition right) =>
        string.Equals(left.ProbeId, right.ProbeId, StringComparison.Ordinal) &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        left.AcceptedExitCodes.SequenceEqual(right.AcceptedExitCodes) &&
        left.Timeout == right.Timeout &&
        left.RequiredOutputSubstrings.SequenceEqual(
            right.RequiredOutputSubstrings,
            StringComparer.Ordinal);
}

public sealed record ResolvedToolDefinition(
    ToolDefinition Definition,
    string DefinitionDigest);
