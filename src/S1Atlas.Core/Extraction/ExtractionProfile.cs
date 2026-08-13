namespace S1Atlas.Core.Extraction;

public sealed record ExtractionProfile(
    int SchemaVersion,
    string ProfileId,
    int ProfileVersion,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    string ExecutableName,
    string OutputFormat,
    TimeSpan Timeout,
    long MaximumRetainedStandardOutputBytes,
    long MaximumRetainedStandardErrorBytes,
    IReadOnlyList<int> AcceptedExitCodes,
    IReadOnlyList<string> RequiredAssemblyIdentities,
    IReadOnlyList<SnapshotInputDefinition> SnapshotInputs,
    IReadOnlyList<string> UnityVersionSources);

public sealed record SnapshotInputDefinition(string RelativePath, string Role);

public sealed record ResolvedExtractionProfile(
    ExtractionProfile Profile,
    string ProfileDigest);
