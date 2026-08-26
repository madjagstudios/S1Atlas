using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

public sealed record ExtractionOptions(
    string? BuildId,
    string? GamePath,
    string? CustomCpp2IlPath,
    string ProfileId,
    bool Retry,
    bool SnapshotInputs,
    bool KeepFailedArtifacts,
    string? InputSnapshotId = null);

public sealed record ExtractionOperationResult(
    ExtractionAttempt Attempt,
    ToolInstance ToolInstance,
    ExtractionInputSource InputSource,
    string? InputSnapshotId,
    bool ProcessWasRun,
    bool IsAuthoritative);

internal interface IExtractionOrchestrationLockLease : IAsyncDisposable
{
    Task UpdateChildProcessIdAsync(
        int childProcessId,
        CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}

internal sealed class ExtractionOrchestratorDependencies
{
    public required Func<CancellationToken, Task> InitializeRepositoryAsync { get; init; }
    public required Func<CancellationToken, Task> RecoverAsync { get; init; }
    public required Func<string?, CancellationToken, Task<GameBuild>> SelectBuildAsync { get; init; }
    public required Func<string, ResolvedExtractionProfile> GetProfile { get; init; }
    public required Func<string, ResolvedValidationPolicy> GetPolicy { get; init; }
    public required Func<string, CancellationToken, Task<IExtractionOrchestrationLockLease>>
        AcquireLockAsync
    { get; init; }
    public required IExtractionRepository AttemptRepository { get; init; }
    public required Func<string?, CancellationToken, Task<ResolvedExtractionTool>>
        ResolveToolAsync
    { get; init; }
    public required Func<
        GameBuild,
        string?,
        string?,
        ExtractionProfile,
        CancellationToken,
        Task<ResolvedExtractionInput>> ResolveInputAsync
    { get; init; }
    public required Func<
        ResolvedExtractionInput,
        GameBuild,
        ExtractionProfile,
        ExtractionFailureStage,
        CancellationToken,
        Task<InputManifest>> CaptureInputManifestAsync
    { get; init; }
    public required Action<InputManifest, InputManifest, GameBuild>
        VerifyInputUnchanged
    { get; init; }
    public Func<
        ResolvedExtractionInput,
        GameBuild,
        ExtractionProfile,
        CancellationToken,
        Task<InputSnapshot>>? CreateInputSnapshotAsync
    { get; init; }
    public required Func<
        OwnedAttemptPaths,
        ExtractionAttempt,
        AttemptExecutionFacts?,
        CancellationToken,
        Task> WriteAttemptDocumentAsync
    { get; init; }
    public required IIl2CppExtractor ProcessExtractor { get; init; }
    public int OwnerProcessId { get; init; } = Environment.ProcessId;
}

internal sealed class ExtractionOrchestrationLockLease(ExtractionLockLease inner)
    : IExtractionOrchestrationLockLease
{
    public Task UpdateChildProcessIdAsync(
        int childProcessId,
        CancellationToken cancellationToken) => inner.UpdateChildProcessIdAsync(
            childProcessId,
            cancellationToken);

    public Task ReleaseAsync(CancellationToken cancellationToken) =>
        inner.ReleaseAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
