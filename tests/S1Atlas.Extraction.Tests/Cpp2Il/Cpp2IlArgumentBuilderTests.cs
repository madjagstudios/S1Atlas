using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Tests.Inputs;
using Xunit;

namespace S1Atlas.Extraction.Tests.Cpp2Il;

public sealed class Cpp2IlArgumentBuilderTests : IDisposable
{
    private readonly string _temporaryDirectory = CreateTemporaryDirectory();

    [Fact]
    public void Build_PreservesLiteralPathsAndReturnsOnlyTheFourTypedArguments()
    {
        var gameRoot = Path.Combine(
            _temporaryDirectory,
            "game with spaces & parentheses (雪) $`literal`");
        Directory.CreateDirectory(gameRoot);
        var outputRoot = CreateAtlasOutputRoot(
            "atlas with spaces & parentheses (雪) $`literal`");

        var arguments = Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile,
            gameRoot,
            outputRoot);

        Assert.Equal(
            [
                $"--game-path={Path.GetFullPath(gameRoot)}",
                "--exe-name=Schedule I",
                $"--output-to={Path.GetFullPath(outputRoot)}",
                "--output-as=dll_il_recovery"
            ],
            arguments);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("profile")]
    [InlineData("adapter")]
    [InlineData("extraction")]
    public void Build_WhenAnyV1IdentityFieldChanges_RejectsProfile(string field)
    {
        var profile = field switch
        {
            "schema" => InputTestFixture.Profile with { SchemaVersion = 2 },
            "profile" => InputTestFixture.Profile with { ProfileVersion = 2 },
            "adapter" => InputTestFixture.Profile with { AdapterVersion = 2 },
            "extraction" => InputTestFixture.Profile with { ExtractionSchemaVersion = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            profile,
            Path.GetFullPath(_temporaryDirectory),
            CreateAtlasOutputRoot()));
    }

    [Fact]
    public void Build_WhenExecutableNameChanges_RejectsProfile()
    {
        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile with { ExecutableName = "Schedule I.exe" },
            Path.GetFullPath(_temporaryDirectory),
            CreateAtlasOutputRoot()));
    }

    [Fact]
    public void Build_WhenOutputFormatChanges_RejectsProfile()
    {
        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile with { OutputFormat = "wasm" },
            Path.GetFullPath(_temporaryDirectory),
            CreateAtlasOutputRoot()));
    }

    [Fact]
    public void Build_WhenOutputIsNotAnOwnedAtlasAttemptOutput_RejectsPath()
    {
        var outside = Path.Combine(_temporaryDirectory, "outside", "output");
        Directory.CreateDirectory(outside);

        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile,
            Path.GetFullPath(_temporaryDirectory),
            outside));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("output")]
    public void Build_WhenAPathIsNotFullyQualified_RejectsRootedRelativeAmbiguity(
        string relativePath)
    {
        var gameRoot = relativePath == "game"
            ? Path.Combine("relative", "game")
            : Path.GetFullPath(_temporaryDirectory);
        var outputRoot = relativePath == "output"
            ? Path.Combine("relative", "output")
            : CreateAtlasOutputRoot();

        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile,
            gameRoot,
            outputRoot));
    }

    [Fact]
    public void Build_WhenOutputAncestorIsAReparsePoint_RejectsPath()
    {
        var outputRoot = CreateAtlasOutputRoot();
        var reparseAncestor = Directory.GetParent(outputRoot)!.Parent!.FullName;
        var inspected = false;

        Assert.Throws<ArgumentException>(() => Cpp2IlArgumentBuilder.Build(
            InputTestFixture.Profile,
            Path.GetFullPath(_temporaryDirectory),
            outputRoot,
            path =>
            {
                var attributes = File.GetAttributes(path);
                if (string.Equals(path, reparseAncestor, StringComparison.OrdinalIgnoreCase))
                {
                    inspected = true;
                    return attributes | FileAttributes.ReparsePoint;
                }

                return attributes;
            }));

        Assert.True(inspected);
    }

    [Fact]
    public void Build_ExposesNoRawArgumentAppendBoundary()
    {
        var buildMethods = typeof(Cpp2IlArgumentBuilder).GetMethods()
            .Where(method => method.Name == nameof(Cpp2IlArgumentBuilder.Build));

        Assert.All(buildMethods, method => Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(string[]) ||
                parameter.ParameterType == typeof(IReadOnlyList<string>)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private string CreateAtlasOutputRoot(string atlasDirectoryName = "atlas")
    {
        var output = Path.Combine(
            _temporaryDirectory,
            atlasDirectoryName,
            "builds",
            "test-build",
            "extractions",
            ".staging",
            Guid.NewGuid().ToString("N"),
            "output");
        Directory.CreateDirectory(output);
        return output;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-cpp2il-argument-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
