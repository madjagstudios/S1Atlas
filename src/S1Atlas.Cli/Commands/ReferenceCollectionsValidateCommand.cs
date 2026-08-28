using System.CommandLine;
using System.Diagnostics;
using S1Atlas.Cli.Output;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Indexing.ReferenceMods;

namespace S1Atlas.Cli.Commands;

internal static class ReferenceCollectionsValidateCommand
{
    public static Command Create(
        ReferenceModManifestLoader loader,
        ReferenceModFileSelector selector,
        ReferenceModInputHasher hasher,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var manifestArgument = new Argument<string>("manifest") { Description = "A local reference-mod collection manifest." };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("validate", "Validate and hash a local reference-mod collection manifest.");
        command.Arguments.Add(manifestArgument);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("reference collections validate", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () => Execute(loader, selector, hasher, parseResult.GetValue(manifestArgument)!, commandOutput, cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        ReferenceModManifestLoader loader,
        ReferenceModFileSelector selector,
        ReferenceModInputHasher hasher,
        string manifestPath,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var manifest = loader.LoadAsync(manifestPath, cancellationToken).GetAwaiter().GetResult();
            var manifestMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            var selected = selector.Select(manifest.Mods);
            var hashes = hasher.HashAsync(selected, cancellationToken).GetAwaiter().GetResult();
            var data = new ReferenceCollectionValidationOutput(
                manifest.CollectionId,
                manifest.CollectionName,
                manifest.Mods.Count,
                hashes.Files.Count,
                hashes.Files.Count(file => file.Kind == ReferenceModInputKind.ManagedAssembly),
                hashes.Files.Count(file => file.Kind != ReferenceModInputKind.ManagedAssembly),
                hashes.CollectionContentSha256,
                Warnings(manifest),
                new ReferencePhaseTimings(manifestMilliseconds, timer.ElapsedMilliseconds, 0));
            return commandOutput.Success(data, writer =>
            {
                writer.WriteLine(
                    $"reference {data.Collection} | mods {data.ModCount} | files {data.FileCount} | " +
                    $"assemblies {data.ManagedAssemblyCount} | documents {data.DocumentCount}");
                writer.WriteLine(
                    $"Phases: manifest {data.Phases.ManifestValidationMilliseconds}ms, " +
                    $"hash {data.Phases.InputHashMilliseconds}ms");
                foreach (var warning in data.Warnings) writer.WriteLine("Warning: " + warning);
            });
        }
        catch (InvalidDataException exception)
        {
            return commandOutput.Failure(1, "InvalidManifest", exception.Message);
        }
    }

    private static IReadOnlyList<string> Warnings(ReferenceCollectionDefinition manifest) =>
        manifest.Mods
            .Where(mod => string.Equals(mod.License, "unknown", StringComparison.OrdinalIgnoreCase))
            .Select(mod => $"Mod '{mod.ModId}' declares an unknown license; provenance remains LocalOnly.")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
