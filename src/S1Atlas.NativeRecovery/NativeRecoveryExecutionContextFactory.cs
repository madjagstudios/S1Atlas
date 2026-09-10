using S1Atlas.Application.Authority;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// The outcome of <see cref="NativeRecoveryExecutionContextFactory.CreateAsync"/>.
/// </summary>
public enum NativeRecoveryExecutionContextStatus
{
    /// <summary>The context was built successfully.</summary>
    Ready,

    /// <summary>The supplied <see cref="InstalledBuildAuthority"/> was not <c>Resolved</c>.</summary>
    AuthorityNotResolved
}

/// <summary>
/// The result of attempting to build a <see cref="NativeRecoveryExecutionContext"/>.
/// </summary>
public sealed record NativeRecoveryExecutionContextResult(
    NativeRecoveryExecutionContextStatus Status,
    NativeRecoveryExecutionContext? Context,
    string? Reason);

/// <summary>
/// Composes a resolved <see cref="InstalledBuildAuthority"/> and the current, freshly-hashed
/// <c>GameAssembly.dll</c> into a <see cref="NativeRecoveryExecutionContext"/>. Read-only: only
/// ever hashes the file at the supplied path; never launches or mutates the game.
/// </summary>
public sealed class NativeRecoveryExecutionContextFactory(IFileHasher fileHasher)
{
    private readonly IFileHasher _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));

    public async Task<NativeRecoveryExecutionContextResult> CreateAsync(
        InstalledBuildAuthority authority,
        string gameAssemblyPath,
        IReadOnlyList<LibraryPin> libraryPins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAssemblyPath);
        ArgumentNullException.ThrowIfNull(libraryPins);

        if (authority.Status != InstalledBuildAuthorityStatus.Resolved ||
            authority.ResolvedBuildId is null ||
            authority.IndexId is null)
        {
            return new NativeRecoveryExecutionContextResult(
                NativeRecoveryExecutionContextStatus.AuthorityNotResolved,
                null,
                authority.Message ??
                    $"The installed build authority did not resolve (status: {authority.Status}).");
        }

        var gameAssemblySha256 = await _fileHasher
            .ComputeSha256Async(gameAssemblyPath, cancellationToken)
            .ConfigureAwait(false);

        var context = new NativeRecoveryExecutionContext(
            authority.ResolvedBuildId,
            authority.IndexId,
            gameAssemblySha256,
            LibraryToolIdentity.ToolName,
            LibraryToolIdentity.ToolVersion(libraryPins),
            LibraryToolIdentity.ComputeToolSha256(libraryPins));

        return new NativeRecoveryExecutionContextResult(
            NativeRecoveryExecutionContextStatus.Ready,
            context,
            null);
    }
}
