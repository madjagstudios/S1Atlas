namespace S1Atlas.Cli.Output;

internal sealed record StatusOutput(
    bool HasCurrentBuild,
    string? BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    DateTimeOffset? CapturedAtUtc,
    int InstalledDependencyCount,
    int DependencyCount);

internal sealed record EnvironmentOutput(
    string BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath,
    IReadOnlyList<DependencyOutput> Dependencies);

internal sealed record DependencyOutput(
    string Kind,
    string? Version,
    string? Path,
    bool IsInstalled);

internal sealed record BuildsOutput(
    IReadOnlyList<BuildOutput> Builds);

internal sealed record BuildOutput(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid);
