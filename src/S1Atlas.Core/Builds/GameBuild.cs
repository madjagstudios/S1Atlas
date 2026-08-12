namespace S1Atlas.Core.Builds;

public sealed record GameBuild(
    string BuildId,
    string? GameVersion,
    string? SteamBuildId,
    string GameAssemblySha256,
    string MetadataSha256,
    DateTimeOffset ScannedAtUtc,
    bool IsValid);
