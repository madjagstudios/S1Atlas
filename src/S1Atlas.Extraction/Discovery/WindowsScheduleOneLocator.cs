using S1Atlas.Core.Discovery;
using System.Text.RegularExpressions;

namespace S1Atlas.Extraction.Discovery;

public sealed class WindowsScheduleOneLocator : IScheduleOneLocator
{
    private readonly IWindowsScheduleOneCandidateSource _candidateSource;

    public WindowsScheduleOneLocator()
        : this(new WindowsScheduleOneCandidateSource())
    {
    }

    internal WindowsScheduleOneLocator(IWindowsScheduleOneCandidateSource candidateSource)
    {
        _candidateSource = candidateSource ??
            throw new ArgumentNullException(nameof(candidateSource));
    }

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

    internal IReadOnlyList<string> GetCandidatePaths(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return [Path.GetFullPath(overridePath)];
        }

        var candidates = new List<string>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoots = new List<string>();
        var seenSteamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidatePath in _candidateSource.GetCandidatePaths())
        {
            TryAddCandidate(candidatePath, candidates, seenCandidates);
            var steamRoot = TryGetSteamRoot(candidatePath);
            if (steamRoot is not null && seenSteamRoots.Add(steamRoot))
            {
                steamRoots.Add(steamRoot);
            }
        }

        foreach (var steamRoot in steamRoots)
        {
            foreach (var libraryRoot in ReadLibraryRoots(steamRoot))
            {
                TryAddCandidate(
                    Path.Combine(
                        libraryRoot,
                        "steamapps",
                        "common",
                        "Schedule I"),
                    candidates,
                    seenCandidates);
            }
        }

        return candidates;
    }

    private static void TryAddCandidate(
        string candidatePath,
        List<string> candidates,
        HashSet<string> seenCandidates)
    {
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidatePath));
            if (seenCandidates.Add(normalized))
            {
                candidates.Add(normalized);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    private static string? TryGetSteamRoot(string candidatePath)
    {
        try
        {
            var scheduleOne = new DirectoryInfo(Path.GetFullPath(candidatePath));
            var common = scheduleOne.Parent;
            var steamApps = common?.Parent;
            var steamRoot = steamApps?.Parent;
            return common is not null && steamApps is not null && steamRoot is not null &&
                string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase)
                    ? steamRoot.FullName
                    : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLibraryRoots(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(
                     content,
                     "\\\"path\\\"\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
                     RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            var value = match.Groups["value"].Value
                .Replace("\\\\", "\\", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal);
            if (!Path.IsPathRooted(value))
            {
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
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

internal interface IWindowsScheduleOneCandidateSource
{
    IReadOnlyList<string> GetCandidatePaths();
}

internal sealed class WindowsScheduleOneCandidateSource
    : IWindowsScheduleOneCandidateSource
{
    public IReadOnlyList<string> GetCandidatePaths()
    {
        var candidates = new List<string>(2);
        AddConventionalCandidate(
            candidates,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddConventionalCandidate(
            candidates,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        return candidates;
    }

    private static void AddConventionalCandidate(
        List<string> candidates,
        string programFiles)
    {
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(
                programFiles,
                "Steam",
                "steamapps",
                "common",
                "Schedule I"));
        }
    }
}
