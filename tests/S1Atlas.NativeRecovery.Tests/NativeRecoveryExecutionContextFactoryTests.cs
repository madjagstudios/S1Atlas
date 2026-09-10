using S1Atlas.Application.Authority;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class NativeRecoveryExecutionContextFactoryTests
{
    private static readonly IReadOnlyList<LibraryPin> Pins =
    [
        new LibraryPin("Samboy063.LibCpp2IL", "2022.1.0-pre-release.21", "test-hash-libcpp2il"),
        new LibraryPin("Iced", "1.21.0", "test-hash-iced"),
    ];

    private const string GameAssemblySha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class FixedFileHasher(string sha256) : IFileHasher
    {
        public string? RequestedPath { get; private set; }

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            RequestedPath = path;
            return Task.FromResult(sha256);
        }
    }

    private static InstalledBuildAuthority ResolvedAuthority(string buildId = "build-1", string indexId = "index-1") =>
        new(
            InstalledBuildAuthorityStatus.Resolved,
            buildId,
            buildId,
            "extraction-1",
            indexId,
            IndexRun: null,
            Message: null);

    [Fact]
    public async Task CreateAsync_ResolvedAuthority_BuildsMatchingContextWithHex64ToolSha256()
    {
        var hasher = new FixedFileHasher(GameAssemblySha256);
        var factory = new NativeRecoveryExecutionContextFactory(hasher);
        var authority = ResolvedAuthority("build-1", "index-1");

        var result = await factory.CreateAsync(authority, "C:/game/GameAssembly.dll", Pins, CancellationToken.None);

        Assert.Equal(NativeRecoveryExecutionContextStatus.Ready, result.Status);
        Assert.NotNull(result.Context);
        Assert.Equal("build-1", result.Context!.CurrentBuildId);
        Assert.Equal("index-1", result.Context.CurrentIndexId);
        Assert.Equal(GameAssemblySha256, result.Context.CurrentGameAssemblySha256);
        Assert.Equal(LibraryToolIdentity.ToolName, result.Context.ToolName);
        Assert.Equal(LibraryToolIdentity.ToolVersion(Pins), result.Context.ToolVersion);
        Assert.Equal(LibraryToolIdentity.ComputeToolSha256(Pins), result.Context.ToolSha256);
        Assert.Equal(64, result.Context.ToolSha256.Length);
        Assert.All(result.Context.ToolSha256, character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.Equal("C:/game/GameAssembly.dll", hasher.RequestedPath);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(InstalledBuildAuthorityStatus.NoCurrentBuild)]
    [InlineData(InstalledBuildAuthorityStatus.BuildNotFound)]
    [InlineData(InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction)]
    [InlineData(InstalledBuildAuthorityStatus.ExtractionIntegrityFailure)]
    [InlineData(InstalledBuildAuthorityStatus.NoCompletedIndex)]
    [InlineData(InstalledBuildAuthorityStatus.IndexBuildMismatch)]
    public async Task CreateAsync_AuthorityNotResolved_ReturnsNegativeResultWithoutHashing(
        InstalledBuildAuthorityStatus status)
    {
        var hasher = new FixedFileHasher(GameAssemblySha256);
        var factory = new NativeRecoveryExecutionContextFactory(hasher);
        var authority = new InstalledBuildAuthority(
            status, "build-1", null, null, null, IndexRun: null, Message: "not resolved");

        var result = await factory.CreateAsync(authority, "C:/game/GameAssembly.dll", Pins, CancellationToken.None);

        Assert.Equal(NativeRecoveryExecutionContextStatus.AuthorityNotResolved, result.Status);
        Assert.Null(result.Context);
        Assert.Equal("not resolved", result.Reason);
        Assert.Null(hasher.RequestedPath);
    }
}
