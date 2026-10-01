using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace S1Atlas.IntegrationTests.Repository;

// Guards the tool-loop scratch ignore rules: a probe file inside every tracked
// Tools/ directory must NOT be ignored (checked with core.ignorecase=true, as
// on Windows checkouts, in both the on-disk and all-lowercase spellings),
// while the documented scratch root must be.
public sealed class ScratchIgnoreGuardTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TrackedToolsDirectories_AreNotIgnored()
    {
        Assert.SkipUnless(GitIsAvailable(), "This test requires git.");
        var toolsDirectories = TrackedToolsDirectories();
        Assert.NotEmpty(toolsDirectories);
        foreach (var directory in toolsDirectories)
        {
            AssertNotIgnored(ProbePath(directory));
            AssertNotIgnored(LowercaseToolsProbePath(directory));
        }
    }

    [Fact]
    public void ToolDevScratchRoot_IsIgnored()
    {
        Assert.SkipUnless(GitIsAvailable(), "This test requires git.");
        AssertIgnored(".tool-dev/packages/__scratch_ignore_probe__.nupkg");
        AssertIgnored(".tool-dev/tools/__scratch_ignore_probe__.dll");
    }

    private static void AssertNotIgnored(string probePath)
    {
        var exitCode = CheckIgnore(probePath);
        Assert.True(
            exitCode == 1,
            $"Expected '{probePath}' to be tracked-eligible but git check-ignore exited {exitCode}.");
    }

    private static void AssertIgnored(string probePath)
    {
        var exitCode = CheckIgnore(probePath);
        Assert.True(
            exitCode == 0,
            $"Expected '{probePath}' to be ignored but git check-ignore exited {exitCode}.");
    }

    private static int CheckIgnore(string probePath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.ignorecase=true");
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(probePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git did not start.");
        var standardError = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode is 0 or 1,
            $"git check-ignore failed for '{probePath}': {standardError}");
        return process.ExitCode;
    }

    private static IReadOnlyList<string> TrackedToolsDirectories()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git did not start.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git ls-files failed: {standardError}");

        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in standardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = path.Replace('\\', '/');
            var segments = normalized.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("Tools", StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add(string.Join('/', segments[..(i + 1)]));
                }
            }
        }

        return [.. directories];
    }

    private static string ProbePath(string directory)
    {
        var extension = directory.StartsWith("config/", StringComparison.Ordinal) ? ".json" : ".cs";
        return $"{directory}/__scratch_ignore_probe__{extension}";
    }

    private static string LowercaseToolsProbePath(string directory)
    {
        return ProbePath(directory.Replace("/Tools/", "/tools/", StringComparison.Ordinal));
    }

    private static bool GitIsAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
