using S1Atlas.Core.Discovery;

namespace S1Atlas.Extraction.Discovery;

public sealed class WindowsScheduleOneLocator : IScheduleOneLocator
{
    public Task<ScheduleOneInstallation?> LocateAsync(
        string? overridePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var candidate in GetCandidatePaths(overridePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var installation = TryCreateInstallation(candidate);
            if (installation is not null)
            {
                return Task.FromResult<ScheduleOneInstallation?>(installation);
            }
        }

        return Task.FromResult<ScheduleOneInstallation?>(null);
    }

    private static IEnumerable<string> GetCandidatePaths(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return Path.GetFullPath(overridePath);
            yield break;
        }

        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(
                programFilesX86,
                "Steam",
                "steamapps",
                "common",
                "Schedule I");
        }

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(
                programFiles,
                "Steam",
                "steamapps",
                "common",
                "Schedule I");
        }
    }

    private static ScheduleOneInstallation? TryCreateInstallation(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return null;
        }

        var executablePath = Path.Combine(rootPath, "Schedule I.exe");
        var gameAssemblyPath = Path.Combine(rootPath, "GameAssembly.dll");
        var globalMetadataPath = Path.Combine(
            rootPath,
            "Schedule I_Data",
            "il2cpp_data",
            "Metadata",
            "global-metadata.dat");

        if (!File.Exists(gameAssemblyPath) || !File.Exists(globalMetadataPath))
        {
            return null;
        }

        return new ScheduleOneInstallation(
            RootPath: rootPath,
            ExecutablePath: executablePath,
            GameAssemblyPath: gameAssemblyPath,
            GlobalMetadataPath: globalMetadataPath,
            ModsPath: Path.Combine(rootPath, "Mods"),
            MelonLoaderPath: Path.Combine(rootPath, "MelonLoader"));
    }
}
