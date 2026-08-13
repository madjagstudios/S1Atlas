namespace S1Atlas.Cli.Configuration;

internal sealed record CliConfigurationPaths(string RootDirectory)
{
    public string ToolDefinitionsDirectory =>
        Path.Combine(RootDirectory, "tools");

    public static CliConfigurationPaths Resolve()
    {
        var appBaseCandidate = Path.Combine(
            AppContext.BaseDirectory,
            "config");
        if (Directory.Exists(Path.Combine(appBaseCandidate, "tools")))
        {
            return new CliConfigurationPaths(
                Path.GetFullPath(appBaseCandidate));
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var repositoryConfig = Path.Combine(current.FullName, "config");
            if (File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")) &&
                Directory.Exists(Path.Combine(repositoryConfig, "tools")))
            {
                return new CliConfigurationPaths(
                    Path.GetFullPath(repositoryConfig));
            }

            current = current.Parent;
        }

        return new CliConfigurationPaths(
            Path.GetFullPath(appBaseCandidate));
    }
}
