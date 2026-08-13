namespace S1Atlas.Core.Environment;

public sealed record InstallationObservation(
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath)
{
    public static InstallationObservation Unknown { get; } =
        new(null, null, null, null, null, null);
}
