namespace S1Atlas.Extraction.Tests.Processes;

internal static class FakeCpp2IlLocator
{
    public static string ExecutablePath { get; } = FindExecutable();

    private static string FindExecutable()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "S1Atlas.sln")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException(
                $"Could not locate S1Atlas.sln above '{AppContext.BaseDirectory}'.");
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executable = Path.Combine(
            current.FullName,
            "tests",
            "S1Atlas.FakeCpp2Il",
            "bin",
            configuration,
            "net8.0",
            "S1Atlas.FakeCpp2Il.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"The source-built fake Cpp2IL apphost was not found at '{executable}'. " +
                "Build S1Atlas.FakeCpp2Il for the active test configuration.",
                executable);
        }

        return executable;
    }
}
