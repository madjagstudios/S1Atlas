using System.Text.Json;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class LibraryToolIdentityTests
{
    [Fact]
    public void ComputeToolSha256_is_64_lowercase_hex_and_order_independent()
    {
        var a = new[] { new LibraryPin("Iced", "1.21.0", "sha512-abc"),
                        new LibraryPin("Samboy063.LibCpp2IL", "2022.1.0-pre-release.21", "sha512-def") };
        var b = new[] { a[1], a[0] };
        var ha = LibraryToolIdentity.ComputeToolSha256(a);
        Assert.Equal(ha, LibraryToolIdentity.ComputeToolSha256(b)); // sorted canonically
        Assert.Matches("^[0-9a-f]{64}$", ha);
    }

    [Fact]
    public void Config_pins_match_packages_lock_json_direct_dependencies()
    {
        var repoRoot = FindRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "config", "tools", "native-recovery-libraries.json");
        var lockPath = Path.Combine(repoRoot, "src", "S1Atlas.NativeRecovery", "packages.lock.json");

        using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
        using var lockDoc = JsonDocument.Parse(File.ReadAllText(lockPath));

        var frameworkDependencies = lockDoc.RootElement.GetProperty("dependencies").EnumerateObject().First().Value;

        var directPins = new Dictionary<string, (string Version, string ContentSha256)>(StringComparer.Ordinal);
        foreach (var dependency in frameworkDependencies.EnumerateObject())
        {
            if (dependency.Value.TryGetProperty("type", out var type) && type.GetString() == "Direct")
            {
                directPins[dependency.Name] = (
                    dependency.Value.GetProperty("resolved").GetString()!,
                    dependency.Value.GetProperty("contentHash").GetString()!);
            }
        }

        var configPins = configDoc.RootElement.GetProperty("pins");
        Assert.Equal(directPins.Count, configPins.GetArrayLength());

        foreach (var pin in configPins.EnumerateArray())
        {
            var packageId = pin.GetProperty("packageId").GetString()!;
            var version = pin.GetProperty("version").GetString()!;
            var contentSha256 = pin.GetProperty("contentSha256").GetString()!;

            Assert.True(
                directPins.TryGetValue(packageId, out var expected),
                $"Unexpected pin '{packageId}' not found in packages.lock.json direct dependencies.");
            Assert.Equal(expected.Version, version);
            Assert.Equal(expected.ContentSha256, contentSha256);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
