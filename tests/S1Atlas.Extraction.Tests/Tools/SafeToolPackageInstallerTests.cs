using System.IO.Compression;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class SafeToolPackageInstallerTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public async Task MaterializeAsync_SingleFile_CopiesOnlyToDeclaredExecutablePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[] { 4, 3, 2, 1 };
        var packagePath = await WritePackageAsync(
            "single.bin",
            bytes,
            cancellationToken);
        var definition = CreateDefinition(
            ToolPackageKind.SingleFile,
            archiveFormat: null,
            executableRelativePath: "nested/Cpp2IL.exe",
            maximumExpandedBytes: bytes.Length,
            maximumFileCount: 1);
        var package = new VerifiedToolPackage(
            packagePath,
            bytes.Length,
            new string('a', 64));
        var stagedRoot = Path.Combine(_temporaryDirectory, "single-install");
        var installer = new SafeToolPackageInstaller();

        var result = await installer.MaterializeAsync(
            definition,
            package,
            stagedRoot,
            cancellationToken);

        Assert.Equal(Path.GetFullPath(stagedRoot), result.InstallRoot);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(stagedRoot, "nested", "Cpp2IL.exe")),
            result.ExecutablePath);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(bytes.Length, result.ExpandedBytes);
        Assert.Equal(
            bytes,
            await File.ReadAllBytesAsync(result.ExecutablePath, cancellationToken));
        Assert.Single(Directory.EnumerateFiles(
            stagedRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MaterializeAsync_SingleFile_WhenDeclaredPathEscapes_Rejects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var packagePath = await WritePackageAsync(
            "single.bin",
            [1],
            cancellationToken);
        var definition = CreateDefinition(
            ToolPackageKind.SingleFile,
            archiveFormat: null,
            executableRelativePath: "../Cpp2IL.exe",
            maximumExpandedBytes: 1,
            maximumFileCount: 1);
        var stagedRoot = Path.Combine(_temporaryDirectory, "unsafe-single");
        var installer = new SafeToolPackageInstaller();

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            installer.MaterializeAsync(
                definition,
                new VerifiedToolPackage(packagePath, 1, new string('a', 64)),
                stagedRoot,
                cancellationToken));

        Assert.Equal("ToolInstallationFailed", exception.Code);
        Assert.False(Directory.Exists(stagedRoot));
    }

    [Fact]
    public async Task MaterializeAsync_Zip_ExtractsContainedRegularFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var packagePath = CreateZip(
            "valid.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1, 2, 3]);
                WriteEntry(archive, "data/settings.txt", [4, 5]);
                archive.CreateEntry("empty-directory/");
            });
        var definition = CreateDefinition(
            ToolPackageKind.Archive,
            ToolArchiveFormat.Zip,
            "Cpp2IL.exe",
            maximumExpandedBytes: 5,
            maximumFileCount: 2);
        var stagedRoot = Path.Combine(_temporaryDirectory, "zip-install");
        var installer = new SafeToolPackageInstaller();

        var result = await installer.MaterializeAsync(
            definition,
            Package(packagePath),
            stagedRoot,
            cancellationToken);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(5, result.ExpandedBytes);
        Assert.True(File.Exists(Path.Combine(stagedRoot, "Cpp2IL.exe")));
        Assert.True(File.Exists(Path.Combine(stagedRoot, "data", "settings.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    public async Task MaterializeAsync_Zip_WhenEntryContainsDotDot_Rejects(
        string entryName)
    {
        await AssertUnsafeZipEntryAsync(entryName);
    }

    [Theory]
    [InlineData("C:/escape.txt")]
    [InlineData("/escape.txt")]
    public async Task MaterializeAsync_Zip_WhenEntryIsAbsolute_Rejects(
        string entryName)
    {
        await AssertUnsafeZipEntryAsync(entryName);
    }

    [Fact]
    public async Task MaterializeAsync_Zip_WhenEntriesCollideCaseInsensitively_Rejects()
    {
        var packagePath = CreateZip(
            "collision.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1]);
                WriteEntry(archive, "DATA.txt", [2]);
                WriteEntry(archive, "data.TXT", [3]);
            });

        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 3,
                maximumFileCount: 3));
    }

    [Fact]
    public async Task MaterializeAsync_Zip_WhenEntryIsUnixSymlink_Rejects()
    {
        var packagePath = CreateZip(
            "symlink.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1]);
                var link = archive.CreateEntry("link");
                link.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var writer = new StreamWriter(link.Open());
                writer.Write("Cpp2IL.exe");
            });

        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 100,
                maximumFileCount: 2));
    }

    [Fact]
    public async Task MaterializeAsync_Zip_WhenExpandedBytesExceedLimit_Rejects()
    {
        var packagePath = CreateZip(
            "expanded.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1, 2, 3]);
                WriteEntry(archive, "data.bin", [4, 5, 6]);
            });

        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 5,
                maximumFileCount: 2));
    }

    [Fact]
    public async Task MaterializeAsync_Zip_WhenRegularFileCountExceedsLimit_Rejects()
    {
        var packagePath = CreateZip(
            "files.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1]);
                WriteEntry(archive, "second.bin", [2]);
            });

        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 2,
                maximumFileCount: 1));
    }

    [Fact]
    public async Task MaterializeAsync_Zip_WhenDeclaredExecutableIsMissing_Rejects()
    {
        var packagePath = CreateZip(
            "missing-executable.zip",
            archive => WriteEntry(archive, "other.bin", [1]));

        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 1,
                maximumFileCount: 1));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task AssertUnsafeZipEntryAsync(string entryName)
    {
        var packagePath = CreateZip(
            $"unsafe-{Guid.NewGuid():N}.zip",
            archive =>
            {
                WriteEntry(archive, "Cpp2IL.exe", [1]);
                WriteEntry(archive, entryName, [2]);
            });
        await AssertZipRejectedAsync(
            packagePath,
            CreateDefinition(
                ToolPackageKind.Archive,
                ToolArchiveFormat.Zip,
                "Cpp2IL.exe",
                maximumExpandedBytes: 2,
                maximumFileCount: 2));
    }

    private async Task AssertZipRejectedAsync(
        string packagePath,
        ResolvedToolDefinition definition)
    {
        var stagedRoot = Path.Combine(
            _temporaryDirectory,
            $"rejected-{Guid.NewGuid():N}");
        var installer = new SafeToolPackageInstaller();

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            installer.MaterializeAsync(
                definition,
                Package(packagePath),
                stagedRoot,
                TestContext.Current.CancellationToken));

        Assert.Equal("ToolInstallationFailed", exception.Code);
        Assert.False(Directory.Exists(stagedRoot));
    }

    private async Task<string> WritePackageAsync(
        string name,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_temporaryDirectory, name);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private string CreateZip(string name, Action<ZipArchive> populate)
    {
        var path = Path.Combine(_temporaryDirectory, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static VerifiedToolPackage Package(string path)
    {
        var length = new FileInfo(path).Length;
        return new VerifiedToolPackage(path, length, new string('a', 64));
    }

    private static ResolvedToolDefinition CreateDefinition(
        ToolPackageKind kind,
        ToolArchiveFormat? archiveFormat,
        string executableRelativePath,
        long maximumExpandedBytes,
        int maximumFileCount)
    {
        var package = new ToolPackageDefinition(
            kind,
            archiveFormat,
            new Uri("https://example.test/package.bin"),
            new Uri("https://example.test/releases/package"),
            "package.bin",
            ExpectedSize: 1,
            Sha256: new string('a', 64),
            ExecutableRelativePath: executableRelativePath,
            Limits: new ToolSafetyLimits(
                MaximumDownloadBytes: 1024 * 1024,
                MaximumExpandedBytes: maximumExpandedBytes,
                MaximumFileCount: maximumFileCount));
        var definition = new ToolDefinition(
            1,
            "cpp2il",
            "Cpp2IL",
            "test-version",
            "win-x64",
            package,
            new ToolLicenseDefinition(
                "MIT",
                new Uri("https://example.test/LICENSE")),
            [
                new ToolProbeDefinition(
                    "help",
                    ["--help"],
                    [0],
                    TimeSpan.FromSeconds(30),
                    [])
            ]);
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }
}
