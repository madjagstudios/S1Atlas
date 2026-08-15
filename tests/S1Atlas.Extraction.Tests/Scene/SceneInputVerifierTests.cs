using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class SceneInputVerifierTests
{
    [Fact]
    public async Task CaptureAsync_MissingPrimaryFile_RejectsInput()
    {
        using var fixture = SceneVerifierFixture.Create();
        File.Delete(fixture.PrimaryPath);
        var verifier = new SceneInputVerifier(new Sha256FileHasher());

        await Assert.ThrowsAsync<IOException>(() => verifier.CaptureAsync(
            fixture.RootPath,
            [fixture.Declaration],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureAsync_TraversalOutsideInstallRoot_RejectsInput()
    {
        using var fixture = SceneVerifierFixture.Create();
        var verifier = new SceneInputVerifier(new Sha256FileHasher());
        var traversal = new SceneContainerDeclaration(
            "../outside/level0",
            []);

        await Assert.ThrowsAsync<IOException>(() => verifier.CaptureAsync(
            fixture.RootPath,
            [traversal],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureAsync_ReparsePointPrimary_RejectsInput()
    {
        using var fixture = SceneVerifierFixture.Create();
        var target = Path.Combine(fixture.RootPath, "target-level0");
        File.Move(fixture.PrimaryPath, target);
        try
        {
            File.CreateSymbolicLink(fixture.PrimaryPath, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var verifier = new SceneInputVerifier(new Sha256FileHasher());

        await Assert.ThrowsAsync<IOException>(() => verifier.CaptureAsync(
            fixture.RootPath,
            [fixture.Declaration],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyAfterParsingAsync_ChangedPrimaryBytesWithStableMetadata_RejectsInput()
    {
        using var fixture = SceneVerifierFixture.Create();
        var verifier = new SceneInputVerifier(new Sha256FileHasher());
        var verified = await verifier.CaptureAsync(
            fixture.RootPath,
            [fixture.Declaration],
            TestContext.Current.CancellationToken);
        MutateBytesWithoutChangingObservedMetadata(fixture.PrimaryPath);

        await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAfterParsingAsync(
            verified,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyAfterParsingAsync_ChangedSidecarBytes_RejectsInput()
    {
        using var fixture = SceneVerifierFixture.Create();
        var verifier = new SceneInputVerifier(new Sha256FileHasher());
        var verified = await verifier.CaptureAsync(
            fixture.RootPath,
            [fixture.Declaration],
            TestContext.Current.CancellationToken);
        MutateBytesWithoutChangingObservedMetadata(fixture.SidecarPath);

        await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAfterParsingAsync(
            verified,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StableInput_ReturnsExactPathsAndManifestAndHashesBeforeAndAfterParsing()
    {
        using var fixture = SceneVerifierFixture.Create();
        var hasher = new CountingFileHasher();
        var verifier = new SceneInputVerifier(hasher);

        var verified = await verifier.CaptureAsync(
            fixture.RootPath,
            [fixture.Declaration],
            TestContext.Current.CancellationToken);
        await verifier.VerifyAfterParsingAsync(
            verified,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(fixture.RootPath), verified.InstallRoot);
        Assert.Matches("^[0-9a-f]{64}$", verified.ManifestDigest);
        var container = Assert.Single(verified.Containers);
        Assert.Equal("Schedule I_Data/level0", container.RelativePath);
        Assert.Equal(Path.GetFullPath(fixture.PrimaryPath), container.PrimaryPath);
        Assert.Equal([Path.GetFullPath(fixture.SidecarPath)], container.SidecarPaths);
        Assert.Equal(new FileInfo(fixture.PrimaryPath).Length, container.ByteCount);
        Assert.Equal("2022.3.62f1", container.UnityVersion);
        Assert.Equal(22, container.SerializedFileVersion);
        Assert.Matches("^[0-9a-f]{64}$", container.Sha256);
        using var sidecars = JsonDocument.Parse(container.SidecarManifest);
        var sidecar = Assert.Single(sidecars.RootElement.EnumerateArray());
        Assert.Equal("Schedule I_Data/level0.resS", sidecar.GetProperty("relativePath").GetString());
        Assert.Equal(new FileInfo(fixture.SidecarPath).Length, sidecar.GetProperty("byteCount").GetInt64());
        Assert.Matches("^[0-9a-f]{64}$", sidecar.GetProperty("sha256").GetString()!);
        Assert.Equal(2, hasher.CallsByPath[fixture.PrimaryPath]);
        Assert.Equal(2, hasher.CallsByPath[fixture.SidecarPath]);
    }

    private static void MutateBytesWithoutChangingObservedMetadata(string path)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastWrite);
    }

    private sealed class CountingFileHasher : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public Dictionary<string, int> CallsByPath { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            CallsByPath[path] = CallsByPath.GetValueOrDefault(path) + 1;
            return await _inner.ComputeSha256Async(path, cancellationToken);
        }
    }

    private sealed class SceneVerifierFixture : IDisposable
    {
        private SceneVerifierFixture(
            SanitizedSerializedFileFixture serializedFile,
            string sidecarPath)
        {
            SerializedFile = serializedFile;
            SidecarPath = sidecarPath;
            Declaration = new SceneContainerDeclaration(
                "Schedule I_Data/level0",
                ["Schedule I_Data/level0.resS"]);
        }

        private SanitizedSerializedFileFixture SerializedFile { get; }
        public string RootPath => SerializedFile.RootPath;
        public string PrimaryPath => SerializedFile.PrimaryPath;
        public string SidecarPath { get; }
        public SceneContainerDeclaration Declaration { get; }

        public static SceneVerifierFixture Create()
        {
            var serializedFile = SanitizedSerializedFileFixture.Create();
            var sidecar = Path.Combine(
                serializedFile.RootPath,
                "Schedule I_Data",
                "level0.resS");
            File.WriteAllBytes(sidecar, [0x10, 0x20, 0x30, 0x40]);
            return new SceneVerifierFixture(serializedFile, sidecar);
        }

        public void Dispose() => SerializedFile.Dispose();
    }
}
