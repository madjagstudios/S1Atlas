namespace S1Atlas.Extraction.Tests.Tools;

internal static class ToolTestFixture
{
    public const string ValidDefinitionJson = """
        {
          "schemaVersion": 1,
          "toolId": "cpp2il",
          "displayName": "Cpp2IL",
          "version": "test-version",
          "platform": "win-x64",
          "package": {
            "kind": "singleFile",
            "archiveFormat": null,
            "sourceUrl": "https://example.test/tool.exe",
            "releaseUrl": "https://example.test/releases/tool",
            "assetName": "tool.exe",
            "expectedSize": 4,
            "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "executableRelativePath": "Cpp2IL.exe",
            "limits": {
              "maximumDownloadBytes": 4,
              "maximumExpandedBytes": 4,
              "maximumFileCount": 1
            }
          },
          "license": {
            "spdxIdentifier": "MIT",
            "sourceUrl": "https://example.test/LICENSE"
          },
          "probes": [
            {
              "probeId": "help",
              "arguments": ["--help"],
              "acceptedExitCodes": [0],
              "timeoutSeconds": 30,
              "requiredOutputSubstrings": []
            },
            {
              "probeId": "output-formats",
              "arguments": ["--list-output-formats"],
              "acceptedExitCodes": [0],
              "timeoutSeconds": 30,
              "requiredOutputSubstrings": ["dll_il_recovery"]
            }
          ]
        }
        """;

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "The S1Atlas repository root could not be located for tool tests.");
    }
}
