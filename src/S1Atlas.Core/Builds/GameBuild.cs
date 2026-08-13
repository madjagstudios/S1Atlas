namespace S1Atlas.Core.Builds;

public sealed record GameBuild(
    string BuildId,
    string GameAssemblySha256,
    string MetadataSha256,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid);
