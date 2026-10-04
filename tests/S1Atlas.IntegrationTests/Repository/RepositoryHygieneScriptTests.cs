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
    [InlineData("scene-index.manifest.json")]
    [InlineData("scene-validation.json")]
    [InlineData("complete.marker")]
    [InlineData("extraction.lock")]
    [InlineData("stdout.log")]
    [InlineData("stderr.log")]
    [InlineData("golden-facts.local.json")]
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
    [InlineData("s1atlas-docs")]
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

    [Theory]
    [InlineData("docs/superpowers/plans/2026-08-29-runtime-proof-protocol.md")]
    [InlineData("docs/worknotes/AT-37.md")]
    [InlineData(".superpowers/task-reports/task-1-report.md")]
    public void Script_ProhibitedPrefix_FailsWithNonZeroExit(string path)
    {
        var exitCode = RunWithTrackedPaths(
        [
            "src/S1Atlas.Cli/Program.cs",
            path
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_PrefixLookAlike_PassesWithZeroExit()
    {
        // Only the exact agent-process directory prefixes are prohibited; a
        // similar name elsewhere in the tree is fine.
        var exitCode = RunWithTrackedPaths(
        [
            "docs/design/superpowered-notes.md",
            "tools/.superpowers-config.yaml"
        ]);

        Assert.Equal(0, exitCode);
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

    [Theory]
    [InlineData("plugin.dll", ".dll")]
    [InlineData("plugin.DLL", ".DLL")]
    [InlineData("utility.exe", ".exe")]
    [InlineData("symbols.pdb", ".pdb")]
    [InlineData("scene.assets", ".assets")]
    [InlineData("scene.resS", ".resS")]
    [InlineData("scene.Ress", ".Ress")]
    [InlineData("scene.bundle", ".bundle")]
    [InlineData("plugin.so", ".so")]
    [InlineData("plugin.dylib", ".dylib")]
    [InlineData("fixtures/nested/plugin.dll", ".dll")]
    public void Script_BinaryExtension_FailsAndNamesExtension(string path, string extension)
    {
        var result = RunWithTrackedPathsAndOutput([path]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"prohibited binary extension '{extension}'", result.Output);
    }

    [Theory]
    [InlineData("plugin.dll.config")]
    [InlineData("symbols.pdb.txt")]
    [InlineData("docs/x.assets.md")]
    public void Script_BinaryExtensionLookalike_Passes(string path)
    {
        Assert.Equal(0, RunWithTrackedPaths([path]));
    }

    [Fact]
    public void Script_BinaryAllowlist_ExemptsOnlyTheExactPath()
    {
        const string emptyAllowlist = "$allowedBinaryPaths = @()";
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains(emptyAllowlist, script);
        var testScript = Path.Combine(Path.GetTempPath(), $"s1atlas-hygiene-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(testScript, script.Replace(
            emptyAllowlist,
            "$allowedBinaryPaths = @('fixtures/approved.dll')",
            StringComparison.Ordinal));
        try
        {
            Assert.Equal(0, RunWithTrackedPathsAndOutput(["fixtures/approved.dll"], testScript).ExitCode);
            Assert.Equal(1, RunWithTrackedPathsAndOutput(["other/approved.dll"], testScript).ExitCode);
            Assert.Equal(1, RunWithTrackedPathsAndOutput(["fixtures/nested/approved.dll"], testScript).ExitCode);
        }
        finally
        {
            File.Delete(testScript);
        }
    }

    [Theory]
    [InlineData("fixtures/*.dll", "fixtures/plugin.dll")]
    [InlineData("fixtures/approved.dll", "fixtures/approved.DLL")]
    [InlineData("GameAssembly.dll", "GameAssembly.dll")]
    [InlineData("decompiled/approved.dll", "decompiled/approved.dll")]
    [InlineData("docs/worknotes/approved.dll", "docs/worknotes/approved.dll")]
    public void Script_BinaryAllowlist_DoesNotBypassOtherRules(string entry, string path)
    {
        const string emptyAllowlist = "$allowedBinaryPaths = @()";
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains(emptyAllowlist, script);
        var testScript = Path.Combine(Path.GetTempPath(), $"s1atlas-hygiene-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(testScript, script.Replace(
            emptyAllowlist,
            $"$allowedBinaryPaths = @('{entry}')",
            StringComparison.Ordinal));
        try
        {
            Assert.Equal(1, RunWithTrackedPathsAndOutput([path], testScript).ExitCode);
        }
        finally
        {
            File.Delete(testScript);
        }
    }

    [Fact]
    public void Script_RealRepository_IsClean()
    {
        var exitCode = Run(arguments: [], workingDirectory: RepositoryRoot);

        Assert.Equal(0, exitCode);
    }

    private static int RunWithTrackedPaths(IReadOnlyList<string> trackedPaths)
        => RunWithTrackedPathsAndOutput(trackedPaths).ExitCode;

    private static (int ExitCode, string Output) RunWithTrackedPathsAndOutput(
        IReadOnlyList<string> trackedPaths, string? scriptPath = null)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-hygiene-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, string.Join('\n', trackedPaths));
        try
        {
            return RunWithOutput(["-TrackedPathsFile", file], RepositoryRoot, scriptPath);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int Run(IReadOnlyList<string> arguments, string workingDirectory)
        => RunWithOutput(arguments, workingDirectory).ExitCode;

    private static (int ExitCode, string Output) RunWithOutput(
        IReadOnlyList<string> arguments, string workingDirectory, string? scriptPath = null)
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
                startInfo.ArgumentList.Add(scriptPath ?? ScriptPath);
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"'{shell}' did not start.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output + error);
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
