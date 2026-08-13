namespace S1Atlas.Core.Extraction;

public sealed record ExtractionRecipe(
    string BuildId,
    string ToolInstanceId,
    string ProfileDigest,
    int AdapterVersion,
    int ExtractionSchemaVersion);
