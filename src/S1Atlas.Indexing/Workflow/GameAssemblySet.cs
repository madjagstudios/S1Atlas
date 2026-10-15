namespace S1Atlas.Indexing.Workflow;

/// <summary>
/// Chooses the assemblies a Schedule I index covers from a Cpp2IL
/// reconstructed directory: Assembly-CSharp.dll first, then ScheduleOne.dll, every
/// ScheduleOne.*.dll (for example ScheduleOne.Core.dll), and the allowlisted
/// third-party runtime assemblies the game ships (FishNet.Runtime.dll), in ordinal
/// order. Unity, System and every other third-party assembly are never indexed.
/// </summary>
public static class GameAssemblySet
{
    public const string PrimaryFileName = "Assembly-CSharp.dll";
    private const string PrimaryAssemblyName = "Assembly-CSharp";
    private const string GameAssemblyPrefix = "ScheduleOne";
    private const string InteropPrefix = "Il2Cpp";
    private static readonly string[] IndexedThirdPartyAssemblies = ["FishNet.Runtime"];

    public static IReadOnlyList<string> Select(string reconstructedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconstructedRoot);
        var primary = Path.Combine(reconstructedRoot, PrimaryFileName);
        if (!Directory.Exists(reconstructedRoot))
            return [primary];
        var additional = Directory.EnumerateFiles(reconstructedRoot, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => !IsPrimary(Path.GetFileName(path)) && IsIndexedAssemblyName(Path.GetFileName(path)))
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

    public static bool IsIndexedAssemblyName(string assemblyName)
    {
        if (IsGameAssemblyName(assemblyName))
            return true;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;
        var name = assemblyName.Trim();
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return IndexedThirdPartyAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static string SourceRelativePath(string fileName) => Path.GetFileNameWithoutExtension(fileName) + ".cs";

    public static string InteropFileName(string fileName) =>
        IsPrimary(fileName) ? PrimaryFileName : InteropPrefix + fileName;

    private static bool IsPrimary(string fileName) =>
        string.Equals(fileName, PrimaryFileName, StringComparison.OrdinalIgnoreCase);
}
