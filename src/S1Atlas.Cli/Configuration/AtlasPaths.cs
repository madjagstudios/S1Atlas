namespace S1Atlas.Cli.Configuration;

public sealed record AtlasPaths(string RootDirectory)
{
    public string DatabasePath => Path.Combine(RootDirectory, "atlas.db");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string ToolsDirectory => Path.Combine(RootDirectory, "tools");

    public string BuildsDirectory => Path.Combine(RootDirectory, "builds");

    public string ExtractionLockPath =>
        Path.Combine(RootDirectory, "extraction.lock");

    public string ToolStagingDirectory =>
        Path.Combine(ToolsDirectory, ".staging");

    public string ToolQuarantineDirectory =>
        Path.Combine(ToolsDirectory, "quarantine");

    public string GetBuildDirectory(string buildId) =>
        Path.Combine(BuildsDirectory, RequireBuildId(buildId));

    public string GetBuildAttemptsDirectory(string buildId) =>
        Path.Combine(GetBuildDirectory(buildId), "attempts");

    public string GetBuildExtractionStagingDirectory(string buildId) =>
        Path.Combine(GetBuildDirectory(buildId), "extractions", ".staging");

    public string GetBuildExtractionsDirectory(string buildId) =>
        Path.Combine(GetBuildDirectory(buildId), "extractions");

    public string GetValidatedExtractionDirectory(string buildId, string extractionId) =>
        Path.Combine(
            GetBuildExtractionsDirectory(buildId),
            RequireExtractionId(extractionId));

    public string GetBuildExtractionQuarantineDirectory(string buildId) =>
        Path.Combine(GetBuildExtractionsDirectory(buildId), "quarantine");

    public string GetBuildInputsDirectory(string buildId) =>
        Path.Combine(GetBuildDirectory(buildId), "inputs");

    public string GetBuildInputStagingDirectory(string buildId) =>
        Path.Combine(GetBuildInputsDirectory(buildId), ".staging");

    public static AtlasPaths FromEnvironment()
    {
        var configuredRoot = System.Environment.GetEnvironmentVariable("S1ATLAS_HOME");
        var root = !string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot
            : Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                "S1Atlas");

        return new AtlasPaths(Path.GetFullPath(root));
    }

    private static string RequireBuildId(string buildId)
    {
        if (buildId is not { Length: 64 } || buildId.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The build ID must be exactly 64 lower-case hexadecimal characters.",
                nameof(buildId));
        }

        return buildId;
    }

    private static string RequireExtractionId(string extractionId)
    {
        if (extractionId is not { Length: 64 } || extractionId.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The extraction ID must be exactly 64 lower-case hexadecimal characters.",
                nameof(extractionId));
        }

        return extractionId;
    }
}
