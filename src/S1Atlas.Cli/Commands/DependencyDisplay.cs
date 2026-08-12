using S1Atlas.Core.Environment;

namespace S1Atlas.Cli.Commands;

internal static class DependencyDisplay
{
    public static string Format(DependencyVersion dependency)
    {
        var name = dependency.Kind switch
        {
            DependencyKind.S1Api => "S1API",
            DependencyKind.S1Mapi => "S1MAPI",
            DependencyKind.MelonLoader => "MelonLoader",
            DependencyKind.Sideload => "Sideload",
            _ => dependency.Kind.ToString()
        };

        if (!dependency.IsInstalled)
        {
            return $"{name}: missing";
        }

        var version = string.IsNullOrWhiteSpace(dependency.Version)
            ? string.Empty
            : $" {dependency.Version}";
        var path = string.IsNullOrWhiteSpace(dependency.Path)
            ? string.Empty
            : $" [{dependency.Path}]";

        return $"{name}: installed{version}{path}";
    }
}
