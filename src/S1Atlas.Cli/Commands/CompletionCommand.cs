using System.CommandLine;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class CompletionCommand
{
    private const string PwshScript = """
        Register-ArgumentCompleter -Native -CommandName s1atlas -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $line = $commandAst.Extent.Text
            s1atlas "[suggest:$cursorPosition]" "$line" 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    private const string BashScript = """
        _s1atlas_completions() {
            local cur="${COMP_WORDS[COMP_CWORD]}"
            local suggestions
            suggestions=$(s1atlas "[suggest:${COMP_POINT}]" "${COMP_LINE}" 2>/dev/null)
            COMPREPLY=($(compgen -W "${suggestions}" -- "${cur}"))
        }
        complete -F _s1atlas_completions s1atlas
        """;

    private const string ZshScript = """
        _s1atlas() {
            local -a completions
            completions=("${(@f)$(s1atlas "[suggest:${CURSOR}]" "${BUFFER}" 2>/dev/null)}")
            compadd -a completions
        }
        compdef _s1atlas s1atlas
        """;

    public static Command Create(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var shellArgument = new Argument<string>("shell") { Description = "The shell to emit a completion script for: pwsh, bash, or zsh." };
        var command = new Command(
            "completion",
            CliExamples.With(
                "Print a shell completion script that completes s1atlas commands and options.",
                "s1atlas completion pwsh >> $PROFILE"));
        command.Arguments.Add(shellArgument);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("completion", false, output, error);
            return CommandExecution.Run(
                () =>
                {
                    var script = parseResult.GetValue(shellArgument)?.ToLowerInvariant() switch
                    {
                        "pwsh" => PwshScript,
                        "bash" => BashScript,
                        "zsh" => ZshScript,
                        _ => null
                    };
                    if (script is null)
                        return commandOutput.Failure(1, "InvalidShell", "Shell must be pwsh, bash, or zsh.");
                    output.Write(script.ReplaceLineEndings("\n") + "\n");
                    return 0;
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }
}
