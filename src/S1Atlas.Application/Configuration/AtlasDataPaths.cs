namespace S1Atlas.Application.Configuration;

public sealed record AtlasDataPaths(string RootDirectory)
{
    public string DatabasePath => Path.Combine(RootDirectory, "atlas.db");

    public static AtlasDataPaths FromEnvironment()
    {
        var configuredRoot = System.Environment.GetEnvironmentVariable("S1ATLAS_HOME");
        var root = !string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot
            : Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                "S1Atlas");

        return new AtlasDataPaths(Path.GetFullPath(root));
    }
}
