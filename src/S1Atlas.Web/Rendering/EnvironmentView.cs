using System.Globalization;
using System.Text;
using S1Atlas.Core.Display;
using S1Atlas.Core.Environment;

namespace S1Atlas.Web.Rendering;

internal static class EnvironmentView
{
    internal const string RecordedNotShown = "recorded (not shown)";

    internal static string Render(EnvironmentSnapshot snapshot)
    {
        var installation = snapshot.Installation;
        var body = new StringBuilder();
        body.Append(Html.PageTitle($"Environment — {snapshot.Build.BuildId}"));
        body.Append("<ul>");
        Fact(body, "build ID", snapshot.Build.BuildId);
        Fact(body, "game assembly SHA-256", snapshot.Build.GameAssemblySha256);
        Fact(body, "metadata SHA-256", snapshot.Build.MetadataSha256);
        Fact(body, "first seen", snapshot.Build.FirstSeenAtUtc.ToString("O"));
        Fact(body, "executable version", installation.ExecutableVersion ?? "not recorded");
        Fact(body, "Steam app ID", installation.SteamAppId ?? "not recorded");
        Fact(body, "Steam build ID", installation.SteamBuildId ?? "not recorded");
        Fact(body, "installation root", RecordedNotShown);
        Fact(body, "GameAssembly path", Display(installation.GameAssemblyPath, installation.InstallationRoot));
        Fact(body, "metadata path", Display(installation.GlobalMetadataPath, installation.InstallationRoot));
        Fact(body, "Atlas version", snapshot.AtlasVersion);
        Fact(body, "environment identity version", snapshot.IdentityVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var dependency in Ordered(snapshot.Dependencies))
        {
            Fact(body, $"dependency {dependency.Kind}", DependencyLine(dependency, installation.InstallationRoot));
        }

        body.Append("</ul>");
        if (snapshot.Dependencies.Count == 0)
        {
            body.Append("<p>No dependencies recorded.</p>");
        }

        return Html.Layout("Environment", body.ToString());
    }

    internal static string Display(string? path, string? installationRoot) =>
        PortalPathDisplay.ToDisplayPath(path, installationRoot) ?? "not recorded";

    private static void Fact(StringBuilder body, string name, string value) =>
        body.Append($"<li>FACT: {Html.Escape(name)} {Html.Escape(value)}.</li>");

    private static IReadOnlyList<DependencyVersion> Ordered(IReadOnlyList<DependencyVersion> dependencies) =>
        dependencies
            .OrderBy(dependency => dependency.Kind)
            .ThenBy(dependency => dependency.Version, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Path, StringComparer.Ordinal)
            .ToArray();

    private static string DependencyLine(DependencyVersion dependency, string? installationRoot)
    {
        if (!dependency.IsInstalled)
        {
            return "missing";
        }

        var line = new StringBuilder("installed");
        if (!string.IsNullOrWhiteSpace(dependency.Version))
        {
            line.Append(CultureInfo.InvariantCulture, $" {dependency.Version}");
        }

        var display = PortalPathDisplay.ToDisplayPath(dependency.Path, installationRoot);
        if (display is not null)
        {
            line.Append(CultureInfo.InvariantCulture, $" [{display}]");
        }

        if (!string.IsNullOrWhiteSpace(dependency.BinarySha256))
        {
            line.Append(CultureInfo.InvariantCulture, $" (sha256 {dependency.BinarySha256})");
        }

        return line.ToString();
    }
}
