using System.CommandLine;
using Microsoft.Data.Sqlite;
using S1Atlas.Application.Composition;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Rendering;

namespace S1Atlas.Cli.Commands;

internal static class DocsGenerateCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var buildOption = new Option<string?>("--build") { Description = "Pin only the Schedule I Installed surface to this build ID." };
        var outputOption = new Option<string?>("--output") { Description = "Output directory; defaults to ./s1atlas-docs/." };
        var command = new Command("generate", "Write a deterministic offline HTML portal.");
        command.Options.Add(buildOption);
        command.Options.Add(outputOption);
        command.SetAction(parseResult => Execute(
            dataRoot,
            parseResult.GetValue(buildOption),
            parseResult.GetValue(outputOption),
            output,
            error,
            cancellationToken));
        return command;
    }

    private static int Execute(
        string dataRoot,
        string? requestedBuildId,
        string? requestedOutput,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var atlasRoot = Path.GetFullPath(dataRoot);
        var outputDirectory = Path.GetFullPath(requestedOutput ?? Path.Combine(Environment.CurrentDirectory, "s1atlas-docs"));
        if (IsEqualOrContained(outputDirectory, atlasRoot))
        {
            error.WriteLine("The docs output directory must be outside the Atlas data root.");
            return 1;
        }

        try
        {
            var services = ReadOnlyAtlasComposition.BuildReadOnlyServices(atlasRoot);
            var model = new PortalModelBuilder().BuildAsync(
                    services,
                    new DocsGenerationRequest(requestedBuildId, outputDirectory),
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            new StaticSiteGenerator().GenerateAsync(model, outputDirectory, cancellationToken)
                .GetAwaiter()
                .GetResult();
            output.WriteLine($"Generated S1Atlas.Docs at {outputDirectory}");
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            error.WriteLine($"Atlas database or indexed data is unavailable ({exception.Message}); run scan or migration first.");
            return 1;
        }
        catch (SqliteException exception)
        {
            error.WriteLine($"Atlas database schema is missing or not the expected version ({exception.Message}); run scan or migration first.");
            return 1;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("database", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"Atlas read-only composition could not open the expected database ({exception.Message}); run scan or migration first.");
            return 1;
        }
    }

    private static bool IsEqualOrContained(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return string.Equals(candidate, Path.TrimEndingDirectorySeparator(root), comparison) || candidate.StartsWith(normalizedRoot, comparison);
    }
}
