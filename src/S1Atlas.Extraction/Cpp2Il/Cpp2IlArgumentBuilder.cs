using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Attempts;

namespace S1Atlas.Extraction.Cpp2Il;

internal static class Cpp2IlArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        ExtractionProfile profile,
        string gameRoot,
        string outputRoot) => Build(
            profile,
            gameRoot,
            outputRoot,
            File.GetAttributes);

    internal static IReadOnlyList<string> Build(
        ExtractionProfile profile,
        string gameRoot,
        string outputRoot,
        Func<string, FileAttributes> getFileAttributes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(getFileAttributes);
        ValidateProfile(profile);
        var normalizedGameRoot = NormalizeFullyQualified(gameRoot, nameof(gameRoot));
        var normalizedOutputRoot = NormalizeFullyQualified(outputRoot, nameof(outputRoot));
        ValidateOwnedOutput(normalizedOutputRoot, getFileAttributes);

        var arguments = new List<string>
        {
            $"--game-path={normalizedGameRoot}",
            "--exe-name=Schedule I",
            $"--output-to={normalizedOutputRoot}",
            "--output-as=dll_il_recovery"
        };
        if (profile.Cpp2IlProcessors.Count > 0)
            arguments.Add("--use-processor=" + string.Join(',', profile.Cpp2IlProcessors));
        return arguments;
    }

    // Version-2 profiles additionally run the attribute processors so reconstructed assemblies
    // keep custom attributes such as [SerializeField]; no other processor set is accepted.
    internal static IReadOnlyList<string> AttributeProcessors { get; } = ["attributeanalyzer", "attributeinjector"];

    private static void ValidateProfile(ExtractionProfile profile)
    {
        if (profile.SchemaVersion != 1 ||
            profile.ProfileVersion is not (1 or 2) ||
            profile.AdapterVersion != 1 ||
            profile.ExtractionSchemaVersion != 1)
        {
            throw new ArgumentException(
                "The typed Cpp2IL adapter supports only version-1 schema, adapter, and extraction profiles at profile version 1 or 2.",
                nameof(profile));
        }

        var expectedProcessors = profile.ProfileVersion == 2 ? AttributeProcessors : [];
        if (!profile.Cpp2IlProcessors.SequenceEqual(expectedProcessors, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Version-1 profiles run no Cpp2IL processors; version-2 profiles run exactly attributeanalyzer,attributeinjector.",
                nameof(profile));
        }

        if (!string.Equals(
                profile.ExecutableName,
                "Schedule I",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The version-1 Cpp2IL adapter requires executable name 'Schedule I'.",
                nameof(profile));
        }

        if (!string.Equals(
                profile.OutputFormat,
                "dll_il_recovery",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The version-1 Cpp2IL adapter requires output format 'dll_il_recovery'.",
                nameof(profile));
        }
    }

    private static string NormalizeFullyQualified(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Cpp2IL paths must be fully qualified.",
                parameterName);
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The Cpp2IL path is invalid.", parameterName, exception);
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                normalized,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Cpp2IL paths must be canonical and cannot contain relative segments.",
                parameterName);
        }

        return normalized;
    }

    private static void ValidateOwnedOutput(
        string outputRoot,
        Func<string, FileAttributes> getFileAttributes)
    {
        try
        {
            if (!string.Equals(
                    Path.GetFileName(outputRoot),
                    "output",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The output leaf is not owned by Atlas.");
            }

            var attemptRoot = Directory.GetParent(outputRoot)?.FullName;
            var stagingRoot = attemptRoot is null
                ? null
                : Directory.GetParent(attemptRoot)?.FullName;
            var extractionsRoot = stagingRoot is null
                ? null
                : Directory.GetParent(stagingRoot)?.FullName;
            var buildRoot = extractionsRoot is null
                ? null
                : Directory.GetParent(extractionsRoot)?.FullName;
            var buildsRoot = buildRoot is null
                ? null
                : Directory.GetParent(buildRoot)?.FullName;
            var atlasRoot = buildsRoot is null
                ? null
                : Directory.GetParent(buildsRoot)?.FullName;
            if (attemptRoot is null || stagingRoot is null || extractionsRoot is null ||
                buildRoot is null || buildsRoot is null || atlasRoot is null ||
                !OwnedAttemptPaths.IsLowerGuidN(Path.GetFileName(attemptRoot)) ||
                !string.Equals(Path.GetFileName(stagingRoot), ".staging", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(extractionsRoot), "extractions", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(buildsRoot), "builds", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The output path is not an Atlas attempt staging output.");
            }

            var paths = OwnedAttemptPaths.Create(
                atlasRoot,
                Path.GetFileName(buildRoot),
                Path.GetFileName(attemptRoot),
                getFileAttributes);
            if (!string.Equals(
                    paths.OutputRoot,
                    outputRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The output path does not match its Atlas-owned allocation.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The output path is not a safe Atlas-owned staging output.",
                nameof(outputRoot),
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                "The output path is not a safe Atlas-owned staging output.",
                nameof(outputRoot),
                exception);
        }
    }
}
