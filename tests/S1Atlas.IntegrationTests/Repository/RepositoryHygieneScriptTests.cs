using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace S1Atlas.IntegrationTests.Repository;

public sealed class RepositoryHygieneScriptTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(
        RepositoryRoot, "scripts", "verify-repository-hygiene.ps1");

    [Fact]
    public void Script_CleanSyntheticList_PassesWithZeroExit()
    {
        var exitCode = RunWithTrackedPaths(
        [
            "src/S1Atlas.Cli/Program.cs",
            "config/validation/managed-assemblies-v1.json",
            "docs/smoke-tests/2026-08-13-schedule-i-cpp2il-extraction.md",
            "README.md"
        ]);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("Cpp2IL.exe")]
    [InlineData("GameAssembly.dll")]
    [InlineData("global-metadata.dat")]
    [InlineData("Assembly-CSharp.dll")]
    [InlineData("atlas.db")]
    [InlineData("atlas.db-wal")]
    [InlineData("atlas.db-shm")]
    [InlineData("installation.json")]
    [InlineData("tool-manifest.json")]
    [InlineData("attempt.json")]
    [InlineData("input-manifest.json")]
    [InlineData("artifact-manifest.json")]
    [InlineData("validation.json")]
    [InlineData("extraction.json")]
    [InlineData("scene-manifest.json")]
    [InlineData("scene-validation.json")]
    [InlineData("complete.marker")]
    [InlineData("extraction.lock")]
    [InlineData("stdout.log")]
    [InlineData("stderr.log")]
    public void Script_ProhibitedBasename_FailsWithNonZeroExit(string basename)
    {
        var exitCode = RunWithTrackedPaths(
        [
            "src/S1Atlas.Cli/Program.cs",
            $"data/builds/abc/{basename}"
        ]);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("candidate-output")]
    [InlineData("retained-output")]
    [InlineData("reconstructed")]
    [InlineData("decompiled")]
    [InlineData(".staging")]
    [InlineData("scene-indexes")]
    [InlineData("scene-staging")]
    [InlineData("scene-recovery")]
    public void Script_ProhibitedSegment_FailsWithNonZeroExit(string segment)
    {
        var exitCode = RunWithTrackedPaths(
        [
            "src/S1Atlas.Cli/Program.cs",
            $"data/builds/abc/{segment}/leaf.txt"
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_InspectsPathsNotDocumentationText()
    {
        // A documentation file may freely mention prohibited names in its content; only
        // the tracked path matters.
        var exitCode = RunWithTrackedPaths(
        [
            "docs/notes-about-GameAssembly.dll-and-complete.marker.md"
        ]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Script_RealRepository_IsClean()
    {
        var exitCode = Run(arguments: [], workingDirectory: RepositoryRoot);

        Assert.Equal(0, exitCode);
    }

    private static int RunWithTrackedPaths(IReadOnlyList<string> trackedPaths)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-hygiene-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, string.Join('\n', trackedPaths));
        try
        {
            return Run(["-TrackedPathsFile", file], RepositoryRoot);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int Run(IReadOnlyList<string> arguments, string workingDirectory)
    {
        foreach (var shell in new[] { "pwsh", "powershell" })
        {
            try
            {
                var startInfo = new ProcessStartInfo(shell)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(ScriptPath);
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"'{shell}' did not start.");
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                // Try the next shell (pwsh may be absent on some machines).
            }
        }

        throw new InvalidOperationException(
            "Neither 'pwsh' nor 'powershell' is available to run the hygiene script.");
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
