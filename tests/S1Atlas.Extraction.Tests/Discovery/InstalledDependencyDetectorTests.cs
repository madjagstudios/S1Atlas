using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Extraction.Discovery;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class InstalledDependencyDetectorTests
{
    [Fact]
    public void Detect_FindsKnownDependenciesAcrossLoaderFolders()
    {
        using var fixture = DependencyFixture.Create();
        fixture.CreateFile("UserLibs", "S1API.dll");
        fixture.CreateFile("UserLibs", "S1MAPI_Il2Cpp.dll");
        fixture.CreateFile("Mods", "Sideload.dll");
        fixture.CreateFile("MelonLoader", "net6", "MelonLoader.dll");

        var detector = new InstalledDependencyDetector();
        var result = detector.Detect(fixture.Installation);

        Assert.Equal(4, result.Count);
        Assert.All(result, dependency => Assert.True(dependency.IsInstalled));
        Assert.Contains(result, dependency => dependency.Kind == DependencyKind.S1Api);
        Assert.Contains(result, dependency => dependency.Kind == DependencyKind.S1Mapi);
        Assert.Contains(result, dependency => dependency.Kind == DependencyKind.MelonLoader);
        Assert.Contains(result, dependency => dependency.Kind == DependencyKind.Sideload);
    }

    [Fact]
    public void Detect_WhenDependenciesAreMissing_ReturnsExplicitMissingEntries()
    {
        using var fixture = DependencyFixture.Create();
        var detector = new InstalledDependencyDetector();

        var result = detector.Detect(fixture.Installation);

        Assert.Equal(4, result.Count);
        Assert.All(result, dependency => Assert.False(dependency.IsInstalled));
    }

    [Fact]
    public void Detect_WhenMultipleMatchesExist_ChoosesDeterministicPath()
    {
        using var fixture = DependencyFixture.Create();
        fixture.CreateFile("UserLibs", "z-last", "S1API.dll");
        var expectedPath = fixture.CreateFile("UserLibs", "a-first", "S1API.dll");
        var detector = new InstalledDependencyDetector();

        var result = detector.Detect(fixture.Installation);

        var dependency = result.Single(item => item.Kind == DependencyKind.S1Api);
        Assert.True(
            string.Equals(
                Path.GetFullPath(expectedPath),
                dependency.Path,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Detect_WhenOneSearchRootCannotBeEnumerated_ContinuesToNextRoot()
    {
        using var fixture = DependencyFixture.Create();
        var expectedPath = fixture.CreateFile("Mods", "S1API.dll");
        var inaccessibleRoot = Path.Combine(fixture.RootPath, "UserLibs");
        var fileEnumerator = new StubDependencyFileEnumerator(rootPath =>
        {
            if (string.Equals(
                Path.GetFullPath(rootPath),
                Path.GetFullPath(inaccessibleRoot),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("simulated inaccessible loader folder");
            }

            return Directory.Exists(rootPath)
                ? Directory.GetFiles(rootPath, "*.dll", SearchOption.TopDirectoryOnly)
                : [];
        });
        var detector = new InstalledDependencyDetector(
            fileEnumerator,
            new StubDependencyVersionReader());

        var result = detector.Detect(fixture.Installation);

        var dependency = result.Single(item => item.Kind == DependencyKind.S1Api);
        Assert.True(dependency.IsInstalled);
        Assert.True(
            string.Equals(
                Path.GetFullPath(expectedPath),
                dependency.Path,
                StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubDependencyFileEnumerator(
        Func<string, IReadOnlyList<string>> enumerate) : IDependencyFileEnumerator
    {
        public IReadOnlyList<string> EnumerateDlls(string rootPath) => enumerate(rootPath);
    }

    private sealed class StubDependencyVersionReader : IDependencyVersionReader
    {
        public string? TryReadVersion(string path) => null;
    }

    private sealed class DependencyFixture : IDisposable
    {
        private DependencyFixture(string rootPath)
        {
            RootPath = rootPath;
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(Path.Combine(rootPath, "Mods"));
            Directory.CreateDirectory(Path.Combine(rootPath, "UserLibs"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Plugins"));
            Directory.CreateDirectory(Path.Combine(rootPath, "MelonLoader"));

            Installation = new ScheduleOneInstallation(
                rootPath,
                Path.Combine(rootPath, "GameAssembly.dll"),
                Path.Combine(rootPath, "Schedule I_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                Path.Combine(rootPath, "Mods"),
                Path.Combine(rootPath, "MelonLoader"));
        }

        public string RootPath { get; }
        public ScheduleOneInstallation Installation { get; }

        public static DependencyFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "S1Atlas.Tests", Guid.NewGuid().ToString("N"));
            return new DependencyFixture(root);
        }

        public string CreateFile(params string[] parts)
        {
            var path = parts.Aggregate(RootPath, Path.Combine);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x01]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
