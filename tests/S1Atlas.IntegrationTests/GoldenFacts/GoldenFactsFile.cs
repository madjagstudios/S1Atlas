using System.Text.Json;

namespace S1Atlas.IntegrationTests.GoldenFacts;

public sealed record SceneFieldFact(
    string Name,
    string Owner,
    string Selector,
    string FieldPath,
    string? ExpectedValue,
    string? ExpectedTargetName);

public sealed record CalleeFact(
    string Name,
    string Selector,
    IReadOnlyList<string> ExpectedCallees);

public sealed record GoldenFactsFile(
    int Version,
    IReadOnlyList<SceneFieldFact> SceneFacts,
    IReadOnlyList<CalleeFact> CalleeFacts)
{
    public const int CurrentVersion = 1;

    public static (GoldenFactsFile? File, string LocalPath) Load()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(), "tests", "S1Atlas.IntegrationTests", "GoldenFacts");
        var localPath = Path.Combine(directory, "golden-facts.local.json");
        if (!File.Exists(localPath))
            return (null, localPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var file = JsonSerializer.Deserialize<GoldenFactsFile>(
            File.ReadAllText(localPath), options);
        if (file is null || file.Version != CurrentVersion)
            throw new InvalidDataException(
                $"Golden facts file '{localPath}' must have version {CurrentVersion}. " +
                "See golden-facts.example.json for the schema.");
        return (file, localPath);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
