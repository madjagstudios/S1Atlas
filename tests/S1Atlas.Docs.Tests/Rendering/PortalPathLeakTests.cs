using System.Text.RegularExpressions;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Rendering;
using Xunit;

namespace S1Atlas.Docs.Tests.Rendering;

// The generated portal may be shared or published, so no generated file may
// carry absolute local paths. The fixture uses distinctive fake roots: one
// Windows-style installation root and one dependency outside that root.
public sealed class PortalPathLeakTests : IAsyncDisposable
{
    private const string RootToken = "fakeroot-zqxv-9182";
    private const string OutsideToken = "elsewhere-qwrt-5511";
    private const string InstallationRoot = $"C:\\{RootToken}\\game";

    private static readonly Regex DriveLetterPath = new(
        @"(?<![A-Za-z])[A-Za-z]:[\\/]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UncPrefix = new(
        @"\\\\[A-Za-z0-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-docs-leak-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Generate_never_emits_absolute_local_paths()
    {
        var output = Path.Combine(_root, "site");
        var seen = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var build = new GameBuild("leak-build-aa01", "assembly-hash", "metadata-hash", seen, true);
        var installation = new InstallationObservation(
            "9.9.leak",
            "1001",
            "2002",
            InstallationRoot,
            $"{InstallationRoot}\\GameAssembly.dll",
            $"{InstallationRoot}\\global-metadata.dat");
        var dependencies = new[]
        {
            new DependencyVersion(
                DependencyKind.MelonLoader, "0.9", $"{InstallationRoot}\\MelonLoader\\net6\\MelonLoader.dll", true, "dep-hash-one"),
            new DependencyVersion(
                DependencyKind.Sideload, "0.2", $"D:\\{OutsideToken}\\tools\\HelperMod.dll", true, "dep-hash-two")
        };
        var model = new PortalSiteModel(
            build.BuildId,
            [],
            new PortalBuildHistoryModel([], []),
            new PortalEnvironmentModel(
                new EnvironmentSnapshot(2, build, installation, dependencies, "test", seen),
                "environment/leak-build-aa01.html"),
            [],
            [],
            []);

        await new StaticSiteGenerator().GenerateAsync(model, output, TestContext.Current.CancellationToken);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(output, file);
            var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            ReportIfPresent(violations, relative, content, InstallationRoot);
            ReportIfPresent(violations, relative, content, RootToken);
            ReportIfPresent(violations, relative, content, OutsideToken);
            ReportMatch(violations, relative, content, DriveLetterPath, "drive-letter path");
            ReportMatch(violations, relative, content, UncPrefix, "UNC prefix");
        }

        Assert.Empty(violations);
    }

    private static void ReportIfPresent(List<string> violations, string relative, string content, string fragment)
    {
        var index = content.IndexOf(fragment, StringComparison.Ordinal);
        if (index >= 0)
        {
            violations.Add($"{relative}: contains {Describe(fragment)} at offset {index}");
        }
    }

    private static void ReportMatch(List<string> violations, string relative, string content, Regex pattern, string label)
    {
        var match = pattern.Match(content);
        if (match.Success)
        {
            violations.Add($"{relative}: contains {label} {Describe(match.Value)} at offset {match.Index}");
        }
    }

    private static string Describe(string value)
    {
        return value.Length <= 80 ? $"'{value}'" : $"'{value[..80]}...'";
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
