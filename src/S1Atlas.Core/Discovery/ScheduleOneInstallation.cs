namespace S1Atlas.Core.Discovery;

public sealed record ScheduleOneInstallation(
    string RootPath,
    string GameAssemblyPath,
    string GlobalMetadataPath,
    string ModsPath,
    string MelonLoaderPath);
