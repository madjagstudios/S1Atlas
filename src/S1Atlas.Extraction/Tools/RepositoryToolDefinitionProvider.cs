using System.Security;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

public sealed class RepositoryToolDefinitionProvider : IToolDefinitionProvider
{
    private readonly string _toolDefinitionDirectory;
    private readonly ToolDefinitionSerializer _serializer = new();

    public RepositoryToolDefinitionProvider(string toolDefinitionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDefinitionDirectory);
        _toolDefinitionDirectory = Path.GetFullPath(toolDefinitionDirectory);
    }

    public IReadOnlyList<ResolvedToolDefinition> GetAll()
    {
        if (!Directory.Exists(_toolDefinitionDirectory))
        {
            throw new ToolOperationException(
                "ToolDefinitionInvalid",
                $"Tool definition directory '{_toolDefinitionDirectory}' does not exist.");
        }

        string[] paths;
        try
        {
            paths = Directory
                .EnumerateFiles(
                    _toolDefinitionDirectory,
                    "*.json",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedFilesystemFailure(exception))
        {
            throw ReadFailure(_toolDefinitionDirectory, exception);
        }

        var definitions = new List<ResolvedToolDefinition>(paths.Length);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception exception) when (IsExpectedFilesystemFailure(exception))
            {
                throw ReadFailure(path, exception);
            }

            var resolved = _serializer.Deserialize(json, path);
            var identity = string.Concat(
                resolved.Definition.ToolId,
                "\u001f",
                resolved.Definition.Platform);
            if (!identities.Add(identity))
            {
                throw new ToolOperationException(
                    "ToolDefinitionInvalid",
                    $"Tool definition '{path}' duplicates the tool/platform pair " +
                    $"'{resolved.Definition.ToolId}/{resolved.Definition.Platform}'.");
            }

            definitions.Add(resolved);
        }

        return definitions;
    }

    public ResolvedToolDefinition GetRequired(string toolId, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var definition = GetAll().FirstOrDefault(candidate =>
            string.Equals(
                candidate.Definition.ToolId,
                toolId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                candidate.Definition.Platform,
                platform,
                StringComparison.OrdinalIgnoreCase));

        return definition ?? throw new ToolOperationException(
            "UnknownTool",
            $"No repository tool definition exists for '{toolId}' on '{platform}'.");
    }

    private static bool IsExpectedFilesystemFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            NotSupportedException;

    private static ToolOperationException ReadFailure(
        string path,
        Exception exception) =>
        new(
            "ToolDefinitionInvalid",
            $"Tool definitions could not be read from '{path}': {exception.Message}",
            exception);
}
