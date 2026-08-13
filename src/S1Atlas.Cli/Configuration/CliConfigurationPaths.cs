namespace S1Atlas.Cli.Configuration;

internal sealed record CliConfigurationPaths(string RootDirectory)
{
    public string ToolDefinitionsDirectory =>
        Path.Combine(RootDirectory, "tools");

    public string ExtractionProfilesDirectory =>
        Path.Combine(RootDirectory, "extraction");

    public string ValidationPoliciesDirectory =>
        Path.Combine(RootDirectory, "validation");

    public static CliConfigurationPaths Resolve()
    {
        var appBaseCandidate = Path.Combine(
            AppContext.BaseDirectory,
            "config");
        if (HasRequiredDirectories(appBaseCandidate))
        {
            return new CliConfigurationPaths(
                Path.GetFullPath(appBaseCandidate));
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var repositoryConfig = Path.Combine(current.FullName, "config");
            if (File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")) &&
                HasRequiredDirectories(repositoryConfig))
            {
                return new CliConfigurationPaths(
                    Path.GetFullPath(repositoryConfig));
            }

            current = current.Parent;
        }

        return new CliConfigurationPaths(
            Path.GetFullPath(appBaseCandidate));
    }

    private static bool HasRequiredDirectories(string root) =>
        Directory.Exists(Path.Combine(root, "tools")) &&
        Directory.Exists(Path.Combine(root, "extraction")) &&
        Directory.Exists(Path.Combine(root, "validation"));
}
