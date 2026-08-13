namespace S1Atlas.Cli.Configuration;

public sealed record AtlasPaths(string RootDirectory)
{
    public string DatabasePath => Path.Combine(RootDirectory, "atlas.db");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string ToolsDirectory => Path.Combine(RootDirectory, "tools");

    public string ToolStagingDirectory =>
        Path.Combine(ToolsDirectory, ".staging");

    public string ToolQuarantineDirectory =>
        Path.Combine(ToolsDirectory, "quarantine");

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
}
