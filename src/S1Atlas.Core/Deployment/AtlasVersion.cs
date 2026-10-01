using System.Reflection;

namespace S1Atlas.Core.Deployment;

public static class AtlasVersion
{
    public static string For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0-unknown";
    }
}
