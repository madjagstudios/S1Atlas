namespace S1Atlas.Indexing.Workflow;

/// <summary>
/// Chooses the game-owned assemblies a Schedule I index covers from a Cpp2IL
/// reconstructed directory: Assembly-CSharp.dll first, then ScheduleOne.dll and every
/// ScheduleOne.*.dll (for example ScheduleOne.Core.dll) in ordinal order. Unity,
/// System and third-party assemblies are never game-owned.
/// </summary>
public static class GameAssemblySet
{
    public const string PrimaryFileName = "Assembly-CSharp.dll";
    private const string PrimaryAssemblyName = "Assembly-CSharp";
    private const string GameAssemblyPrefix = "ScheduleOne";
    private const string InteropPrefix = "Il2Cpp";

    public static IReadOnlyList<string> Select(string reconstructedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconstructedRoot);
        var primary = Path.Combine(reconstructedRoot, PrimaryFileName);
        if (!Directory.Exists(reconstructedRoot))
            return [primary];
        var additional = Directory.EnumerateFiles(reconstructedRoot, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => !IsPrimary(Path.GetFileName(path)) && IsGameAssemblyName(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);
        return [primary, .. additional];
    }

    public static bool IsGameAssemblyName(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;
        var name = assemblyName.Trim();
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.Equals(name, PrimaryAssemblyName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, GameAssemblyPrefix, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(GameAssemblyPrefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string SourceRelativePath(string fileName) => Path.GetFileNameWithoutExtension(fileName) + ".cs";

    public static string InteropFileName(string fileName) =>
        IsPrimary(fileName) ? PrimaryFileName : InteropPrefix + fileName;

    private static bool IsPrimary(string fileName) =>
        string.Equals(fileName, PrimaryFileName, StringComparison.OrdinalIgnoreCase);
}
