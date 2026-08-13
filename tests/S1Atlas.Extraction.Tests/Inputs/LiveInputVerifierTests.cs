using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Inputs;
using Xunit;

namespace S1Atlas.Extraction.Tests.Inputs;

public sealed class LiveInputVerifierTests
{
    [Fact]
    public async Task CaptureAsync_MatchingInput_RecordsCanonicalRelativeFacts()
    {
        using var fixture = InputTestFixture.Create();
        var verifier = new LiveInputVerifier(new Sha256FileHasher());
        var input = CreateResolvedInput(fixture);

        var manifest = await verifier.CaptureAsync(
            input,
            fixture.Build,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, manifest.Entries.Count);
        var assembly = Assert.Single(
            manifest.Entries,
            file => file.Role == "gameAssembly");
        Assert.Equal("GameAssembly.dll", assembly.RelativePath);
        Assert.Equal(fixture.Build.GameAssemblySha256, assembly.Sha256);
        Assert.Equal(new FileInfo(fixture.GameAssemblyPath).Length, assembly.Size);
        Assert.Equal(
            File.GetLastWriteTimeUtc(fixture.GameAssemblyPath),
            assembly.LastWriteUtc.UtcDateTime);
        Assert.Equal(
            fixture.Build.MetadataSha256,
            Assert.Single(manifest.Entries, file => file.Role == "globalMetadata").Sha256);
        Assert.All(manifest.Entries, file => Assert.False(Path.IsPathRooted(file.RelativePath)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureAsync_WhenBuildHashDoesNotMatch_RejectsBeforeProcessStart(
        bool mismatchAssembly)
    {
        using var fixture = InputTestFixture.Create();
        var build = mismatchAssembly
            ? fixture.Build with { GameAssemblySha256 = new string('a', 64) }
            : fixture.Build with { MetadataSha256 = new string('b', 64) };
        var verifier = new LiveInputVerifier(new Sha256FileHasher());

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureStage.PreRunInputVerification, exception.Stage);
        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
    }

    [Fact]
    public async Task CaptureAsync_WhenRequiredFileIsMissing_RejectsInput()
    {
        using var fixture = InputTestFixture.Create(includeExecutable: false);
        var verifier = new LiveInputVerifier(new Sha256FileHasher());

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                fixture.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
    }

    [Fact]
    public async Task CaptureAsync_WhenRequiredPathIsDirectory_RequiresRegularFile()
    {
        using var fixture = InputTestFixture.Create(includeExecutable: false);
        Directory.CreateDirectory(fixture.ExecutablePath);
        var verifier = new LiveInputVerifier(new Sha256FileHasher());

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                fixture.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
    }

    [Fact]
    public async Task CaptureAsync_WhenRequiredFileIsReparsePoint_RejectsInput()
    {
        using var fixture = InputTestFixture.Create(includeExecutable: false);
        var target = Path.Combine(fixture.RootPath, "support-target.bin");
        File.WriteAllBytes(target, [0x01]);
        try
        {
            File.CreateSymbolicLink(fixture.ExecutablePath, target);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var verifier = new LiveInputVerifier(new Sha256FileHasher());
        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                fixture.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
    }

    [Fact]
    public async Task CaptureAsync_CancellationBetweenFiles_StopsBeforeNextHash()
    {
        using var fixture = InputTestFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var hasher = new CancelAfterFirstHasher(cancellation);
        var verifier = new LiveInputVerifier(hasher);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                fixture.Build,
                InputTestFixture.Profile,
                cancellation.Token));

        Assert.Equal(1, hasher.CallCount);
    }

    [Fact]
    public async Task CaptureAsync_WhenFileChangesDuringFirstHash_RetriesStableObservation()
    {
        using var fixture = InputTestFixture.Create();
        var finalBytes = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var hasher = new MutatingHasher(
            fixture.GameAssemblyPath,
            finalBytes,
            mutateEveryCall: false);
        var verifier = new LiveInputVerifier(hasher);
        var build = fixture.Build with
        {
            GameAssemblySha256 = InputTestFixture.Hash(finalBytes)
        };

        var manifest = await verifier.CaptureAsync(
            CreateResolvedInput(fixture),
            build,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, hasher.TargetCallCount);
        Assert.Equal(
            build.GameAssemblySha256,
            Assert.Single(manifest.Entries, file => file.Role == "gameAssembly").Sha256);
    }

    [Fact]
    public async Task CaptureAsync_WhenFileNeverStabilizes_RejectsBeforeProcessStart()
    {
        using var fixture = InputTestFixture.Create();
        var hasher = new MutatingHasher(
            fixture.GameAssemblyPath,
            [0x11, 0x22, 0x33, 0x44, 0x55],
            mutateEveryCall: true);
        var verifier = new LiveInputVerifier(hasher);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => verifier.CaptureAsync(
                CreateResolvedInput(fixture),
                fixture.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
    }

    [Fact]
    public void VerifyUnchanged_UsesHashesRatherThanSizeOrTimestamps()
    {
        using var fixture = InputTestFixture.Create();
        var verifier = new LiveInputVerifier(new Sha256FileHasher());
        var pre = CreateComparisonManifest(fixture, sizeDelta: 0, hashOverride: null);
        var post = CreateComparisonManifest(fixture, sizeDelta: 100, hashOverride: null);

        verifier.VerifyUnchanged(pre, post, fixture.Build);
    }

    [Fact]
    public void VerifyUnchanged_WhenHashDiffers_ReportsPostRunInputChange()
    {
        using var fixture = InputTestFixture.Create();
        var verifier = new LiveInputVerifier(new Sha256FileHasher());
        var pre = CreateComparisonManifest(fixture, sizeDelta: 0, hashOverride: null);
        var post = CreateComparisonManifest(
            fixture,
            sizeDelta: 0,
            hashOverride: new string('c', 64));

        var exception = Assert.Throws<ExtractionOperationException>(
            () => verifier.VerifyUnchanged(pre, post, fixture.Build));

        Assert.Equal(ExtractionFailureStage.PostRunInputVerification, exception.Stage);
        Assert.Equal(ExtractionFailureCode.InputChangedDuringExtraction, exception.Code);
    }

    private static ResolvedExtractionInput CreateResolvedInput(InputTestFixture fixture) =>
        new(
            ExtractionInputSource.Live,
            fixture.RootPath,
            fixture.GameAssemblyPath,
            fixture.MetadataPath,
            fixture.ExecutablePath,
            fixture.UnityVersionPath,
            InputSnapshotId: null);

    private static InputManifest CreateComparisonManifest(
        InputTestFixture fixture,
        long sizeDelta,
        string? hashOverride)
    {
        var now = DateTimeOffset.UtcNow.AddHours(sizeDelta);
        return new InputManifest(
        [
            new(
                "GameAssembly.dll",
                "gameAssembly",
                4 + sizeDelta,
                hashOverride ?? fixture.Build.GameAssemblySha256,
                now),
            new(
                "Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat",
                "globalMetadata",
                3 + sizeDelta,
                fixture.Build.MetadataSha256,
                now)
        ]);
    }

    private sealed class CancelAfterFirstHasher(CancellationTokenSource cancellation) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public int CallCount { get; private set; }

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            cancellation.Cancel();
            return hash;
        }
    }

    private sealed class MutatingHasher(
        string targetPath,
        byte[] replacement,
        bool mutateEveryCall) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        private byte[] _next = replacement;

        public int TargetCallCount { get; private set; }

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                TargetCallCount++;
                if (mutateEveryCall || TargetCallCount == 1)
                {
                    File.WriteAllBytes(path, _next);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(TargetCallCount));
                    if (mutateEveryCall)
                    {
                        _next = [.. _next, (byte)TargetCallCount];
                    }
                }
            }

            return hash;
        }
    }
}
