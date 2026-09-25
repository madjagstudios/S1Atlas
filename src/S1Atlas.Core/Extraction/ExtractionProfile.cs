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
    IReadOnlyList<string> UnityVersionSources,
    IReadOnlyList<string>? Cpp2IlProcessors = null)
{
    // Cpp2IL processing layers, in execution order. Empty for profiles that run none.
    public IReadOnlyList<string> Cpp2IlProcessors { get; init; } = Cpp2IlProcessors ?? [];
}

public sealed record SnapshotInputDefinition(string RelativePath, string Role);

public sealed record ResolvedExtractionProfile(
    ExtractionProfile Profile,
    string ProfileDigest);
