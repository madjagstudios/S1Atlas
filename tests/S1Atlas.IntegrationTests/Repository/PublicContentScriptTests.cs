using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace S1Atlas.IntegrationTests.Repository;

// Gate-test fixtures: the violation patterns below (AI tool names, machine
// paths, ticket keys, workflow phrases, tracker URLs) are intentional
// synthetic inputs proving the public-content gate fires. This file is
// exempt from the gate's own content rules for exactly that reason.
public sealed class PublicContentScriptTests
{
    private const string CleanMessage = "Add a CI gate for public-repo content (AT-102)";
    private const string CleanBranch = "ci/at-102-public-content-gate";

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(
        RepositoryRoot, "scripts", "verify-public-content.ps1");

    [Fact]
    public void Script_CleanInputs_PassWithZeroExit()
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", "var ok = true;"),
            CleanBranch);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Script_CoAuthoredByTrailer_FailsWithNonZeroExit()
    {
        var exitCode = RunGate(
            "Add a feature (AT-102)\n\nCo-Authored-By: Example <example@example.com>",
            DiffFor("src/S1Atlas.Mcp/Program.cs", "var ok = true;"),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_GeneratedWithLine_FailsWithNonZeroExit()
    {
        var exitCode = RunGate(
            "Add a feature (AT-102)\n\nGenerated with assistance.",
            DiffFor("src/S1Atlas.Mcp/Program.cs", "var ok = true;"),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("claude")]
    [InlineData("Codex")]
    [InlineData("Copilot")]
    [InlineData("ChatGPT")]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    [InlineData("Gemini")]
    [InlineData("GPT-4")]
    [InlineData("GPT-5")]
    public void Script_AiToolNameInMessage_FailsWithNonZeroExit(string name)
    {
        var exitCode = RunGate(
            $"Add a feature with {name} (AT-102)",
            DiffFor("src/S1Atlas.Mcp/Program.cs", "var ok = true;"),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData(@"var root = ""C:\Users\example\data"";")]
    [InlineData(@"var root = ""D:\Users\example\data"";")]
    [InlineData("// see /Users/example/data/ for input")]
    [InlineData("// see /home/example/data/ for input")]
    public void Script_MachinePathInAddedLine_FailsWithNonZeroExit(string line)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", line),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("// see OC-12 for details")]
    [InlineData("// see AB-1 for details")]
    public void Script_NonAtTicketKeyInAddedLine_FailsWithNonZeroExit(string line)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", line),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("// For agentic workers: start here")]
    [InlineData("// REQUIRED SUB-SKILL: testing")]
    [InlineData("// superpowers: testing")]
    public void Script_AgentWorkflowPhraseInAddedLine_FailsWithNonZeroExit(string line)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", line),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_TrackerUrlInAddedLine_FailsWithNonZeroExit()
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", "// https://example.atlassian.net/browse/AT-102"),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("GPT-5")]
    public void Script_AiToolNameInAddedLine_FailsWithNonZeroExit(string name)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", $"// reviewed with {name}"),
            CleanBranch);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_StandardIdentifiersAndAtKeys_PassWithZeroExit()
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor(
                "src/S1Atlas.Mcp/Program.cs",
                "// Encoded as UTF-8; hashed with SHA-256 (AT-123).",
                "// See also UTF-16, SHA-1, SHA-512, ISO-8601, and x86-64 notes."),
            CleanBranch);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Script_RemovedAndContextLines_AreNeverChecked()
    {
        var diff = string.Join('\n', new[]
        {
            "diff --git a/src/S1Atlas.Mcp/Program.cs b/src/S1Atlas.Mcp/Program.cs",
            "--- a/src/S1Atlas.Mcp/Program.cs",
            "+++ b/src/S1Atlas.Mcp/Program.cs",
            "@@ -1,3 +1,2 @@",
            " context with OC-12 and Claude",
            @"-var root = ""C:\Users\example\data"";",
            "-// superpowers: testing",
            "+var ok = true;"
        });
        var exitCode = RunGate(CleanMessage, diff, CleanBranch);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Script_AiNameWordBoundary_PassesWithZeroExit()
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", "// the declaude helper passes"),
            CleanBranch);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Script_AllowlistedDoc_AllowsAiNameButNothingElse()
    {
        var aiName = RunGate(
            CleanMessage,
            DiffFor("docs/USAGE.md", "Claude and Codex use the same MCP server."),
            CleanBranch);

        Assert.Equal(0, aiName);

        var machinePath = RunGate(
            CleanMessage,
            DiffFor("docs/USAGE.md", @"See C:\Users\example\data for input."),
            CleanBranch);

        Assert.Equal(1, machinePath);
    }

    [Theory]
    [InlineData("scripts/verify-public-content.ps1")]
    [InlineData("scripts/public-content-allowlist.txt")]
    [InlineData("tests/S1Atlas.IntegrationTests/Repository/PublicContentScriptTests.cs")]
    public void Script_SelfExemptPaths_PassWithZeroExit(string path)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor(
                path,
                @"# pattern against C:\Users\example and OC-12",
                "# reviewed with Claude; see https://example.atlassian.net/x"),
            CleanBranch);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("claude/experiment")]
    [InlineData("feat/Claude-fix")]
    public void Script_AiToolNameInBranch_FailsWithNonZeroExit(string branch)
    {
        var exitCode = RunGate(
            CleanMessage,
            DiffFor("src/S1Atlas.Mcp/Program.cs", "var ok = true;"),
            branch);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Script_EmptyBaseRange_PassesWithZeroExit()
    {
        var exitCode = Run(["-BaseRef", "HEAD"], RepositoryRoot);

        Assert.Equal(0, exitCode);
    }

    private static string DiffFor(string path, params string[] addedLines)
    {
        var lines = new List<string>
        {
            $"diff --git a/{path} b/{path}",
            $"--- a/{path}",
            $"+++ b/{path}",
            $"@@ -1,1 +1,{1 + addedLines.Length} @@",
            " context"
        };
        lines.AddRange(addedLines.Select(line => "+" + line));
        return string.Join('\n', lines);
    }

    private static int RunGate(string? messages, string? diff, string? branchName)
    {
        var files = new List<string>();
        var arguments = new List<string>();
        try
        {
            if (messages is not null)
            {
                var messageFile = WriteTempFile("s1atlas-public-messages", messages);
                files.Add(messageFile);
                arguments.Add("-CommitMessagesFile");
                arguments.Add(messageFile);
            }

            if (diff is not null)
            {
                var diffFile = WriteTempFile("s1atlas-public-diff", diff);
                files.Add(diffFile);
                arguments.Add("-DiffFile");
                arguments.Add(diffFile);
            }

            if (branchName is not null)
            {
                arguments.Add("-BranchName");
                arguments.Add(branchName);
            }

            return Run(arguments, RepositoryRoot);
        }
        finally
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
    }

    private static string WriteTempFile(string prefix, string content)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, content);
        return file;
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
            "Neither 'pwsh' nor 'powershell' is available to run the public-content script.");
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
