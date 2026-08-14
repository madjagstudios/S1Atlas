using System.CommandLine;
using System.Text;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class SourceCommand
{
    private const long TerminalByteLimit = 1_048_576;

    public static Command Create(
        IndexQueryService service,
        IAtlasRepository repository,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var queryArgument = new Argument<string>("query") { Description = "A symbol selector." };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all." };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of resolution candidates to consider.",
            DefaultValueFactory = _ => 50
        };
        var contextOption = new Option<int>("--context")
        {
            Description = "Lines of context before and after the selected source span.",
            DefaultValueFactory = _ => 5
        };
        var fileOption = new Option<bool>("--file")
        {
            Description = "Return the complete hash-verified source file instead of a focused snippet."
        };
        var outputOption = new Option<string?>("--output")
        {
            Description = "Write the complete hash-verified source file to this path."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("source", "Show integrity-checked source for one resolved symbol.");
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(limitOption);
        command.Options.Add(contextOption);
        command.Options.Add(fileOption);
        command.Options.Add(outputOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("source", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () => Execute(
                    service,
                    repository,
                    dataRoot,
                    parseResult.GetValue(queryArgument)!,
                    parseResult.GetValue(codebaseOption),
                    parseResult.GetValue(channelOption),
                    parseResult.GetValue(limitOption),
                    parseResult.GetValue(contextOption),
                    parseResult.GetValue(fileOption),
                    parseResult.GetValue(outputOption),
                    commandOutput,
                    cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        IndexQueryService service,
        IAtlasRepository repository,
        string dataRoot,
        string query,
        string? codebase,
        string? channel,
        int limit,
        int context,
        bool fullFile,
        string? destination,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
            return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");
        if (context < 0)
            return commandOutput.Failure(1, "InvalidContext", "--context cannot be negative.");

        repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        var options = IndexQueryCommandFactory.ParseOptions(codebase, channel, limit);

        SourceSnippetResolutionResult resolution;
        try
        {
            resolution = service.SourceAsync(query, options, context, cancellationToken).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException exception)
        {
            return commandOutput.Failure(1, "SourceUnavailable", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return commandOutput.Failure(1, "SourceIntegrityFailure", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return commandOutput.Failure(1, "InvalidCodebaseChannel", exception.Message);
        }

        if (resolution.Resolution.Status == SymbolResolutionStatus.Ambiguous)
        {
            return commandOutput.Failure(
                1,
                "AmbiguousSymbol",
                "The symbol selector matched multiple candidates. Use an exact symbol ID or signature.",
                new IndexQueryFailureData(resolution.Resolution.Candidates));
        }
        if (resolution.Resolution.Status == SymbolResolutionStatus.NotFound)
        {
            return commandOutput.Failure(
                1,
                "SymbolNotFound",
                "No indexed symbol matched the selector.",
                new IndexQueryFailureData([]));
        }
        if (resolution.Snippet is null)
            return commandOutput.Failure(1, "SourceUnavailable", "The selected symbol has no indexed source location.");

        var snippet = resolution.Snippet;
        byte[]? verifiedBytes = null;
        if (fullFile || !string.IsNullOrWhiteSpace(destination))
        {
            try
            {
                verifiedBytes = new VerifiedIndexedSourceReader().ReadAsync(
                    dataRoot,
                    options.Codebase,
                    ParseChannel(snippet.Symbol.Channel),
                    snippet.IndexId,
                    snippet.RelativePath,
                    snippet.Sha256,
                    cancellationToken).GetAwaiter().GetResult();
            }
            catch (FileNotFoundException exception)
            {
                return commandOutput.Failure(1, "SourceUnavailable", exception.Message);
            }
            catch (InvalidDataException exception)
            {
                return commandOutput.Failure(1, "SourceIntegrityFailure", exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return commandOutput.Failure(1, "InvalidCodebaseChannel", exception.Message);
            }
        }

        string? writtenPath = null;
        string? displayedText;
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var destinationPath = Path.GetFullPath(destination);
            var current = repository.GetCurrentSnapshotAsync(cancellationToken).GetAwaiter().GetResult();
            if (current is not null && IsEqualToOrDescendantOf(destinationPath, current.Installation.InstallationRoot))
            {
                return commandOutput.Failure(
                    1,
                    "ReadOnlyGameInstallation",
                    "Source output cannot be written into the detected Schedule I installation.");
            }

            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllBytes(destinationPath, verifiedBytes!);
            writtenPath = destinationPath;
            displayedText = null;
        }
        else if (fullFile)
        {
            if (verifiedBytes!.LongLength > TerminalByteLimit)
            {
                return commandOutput.Failure(
                    1,
                    "SourceTooLargeForTerminal",
                    $"The selected source file exceeds the {TerminalByteLimit} byte terminal limit. Provide --output <path>.");
            }
            displayedText = Encoding.UTF8.GetString(verifiedBytes);
        }
        else
        {
            displayedText = snippet.Text;
        }

        var data = new SourceCliOutput(
            snippet.Symbol,
            snippet.IndexId,
            snippet.RelativePath,
            snippet.Sha256,
            snippet.ByteCount,
            snippet.Location,
            snippet.ContextBefore,
            snippet.ContextAfter,
            snippet.BodyRecoveryStatus,
            snippet.Provenance,
            fullFile || writtenPath is not null,
            writtenPath,
            displayedText);
        return commandOutput.Success(data, writer => WriteHuman(data, writer));
    }

    private static void WriteHuman(SourceCliOutput data, TextWriter writer)
    {
        writer.WriteLine($"Symbol:        {data.Symbol.QualifiedName} [{data.Symbol.SymbolId}]");
        writer.WriteLine($"Signature:     {data.Symbol.Signature}");
        writer.WriteLine($"Source:        {data.RelativePath}");
        writer.WriteLine($"SHA-256:       {data.Sha256}");
        writer.WriteLine($"Range:         {FormatRange(data.Location)}");
        writer.WriteLine($"Body recovery: {data.BodyRecoveryStatus?.ToString() ?? "n/a"}");
        writer.WriteLine($"Provenance:    {data.Provenance}");
        if (data.OutputPath is not null) writer.WriteLine($"Written:       {data.OutputPath}");
        if (data.Text is not null)
        {
            writer.WriteLine();
            writer.Write(data.Text);
            if (!data.Text.EndsWith('\n')) writer.WriteLine();
        }
    }

    private static string FormatRange(SourceLocationQueryResult location) =>
        $"{location.StartLine}:{location.StartColumn}-{location.EndLine ?? location.StartLine}:{location.EndColumn ?? location.StartColumn}";

    private static CodeChannel ParseChannel(string channel) =>
        Enum.TryParse<CodeChannel>(channel, ignoreCase: true, out var parsed)
            ? parsed
            : throw new NotSupportedException($"Indexed source channel '{channel}' is not supported.");

    private static bool IsEqualToOrDescendantOf(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullCandidate, fullRoot, comparison)) return true;
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) ||
            (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar &&
             fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison));
    }

    private sealed record SourceCliOutput(
        SymbolQueryResult Symbol,
        string IndexId,
        string RelativePath,
        string Sha256,
        long ByteCount,
        SourceLocationQueryResult Location,
        int ContextBefore,
        int ContextAfter,
        BodyRecoveryStatus? BodyRecoveryStatus,
        string Provenance,
        bool FullFile,
        string? OutputPath,
        string? Text);
}
