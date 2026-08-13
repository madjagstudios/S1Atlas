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
    bool KeepFailedArtifacts);

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

public sealed class ExtractionOrchestrator
{
    internal const string ValidationPolicyId = "managed-assemblies-v1";

    private readonly string _dataRoot;
    private readonly ExtractionOrchestratorDependencies _dependencies;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _attemptIdFactory;
    private readonly Action<string, string> _moveDirectory;

    internal ExtractionOrchestrator(
        string dataRoot,
        IAtlasRepository atlasRepository,
        IExtractionRepository extractionRepository,
        IExtractionProfileProvider profileProvider,
        IValidationPolicyProvider validationPolicyProvider,
        ExtractionToolResolver toolResolver,
        ExtractionInputResolver inputResolver,
        LiveInputVerifier inputVerifier,
        Func<GameBuild, InputSnapshotService>? inputSnapshotServiceFactory,
        AttemptDocumentStore attemptDocumentStore,
        ExtractionLock extractionLock,
        ExtractionRecoveryService recoveryService,
        IIl2CppExtractor processExtractor,
        TimeProvider? timeProvider = null)
        : this(
            dataRoot,
            CreateDependencies(
                atlasRepository,
                extractionRepository,
                profileProvider,
                validationPolicyProvider,
                toolResolver,
                inputResolver,
                inputVerifier,
                inputSnapshotServiceFactory,
                attemptDocumentStore,
                extractionLock,
                recoveryService,
                processExtractor),
            timeProvider)
    {
    }

    internal ExtractionOrchestrator(
        string dataRoot,
        ExtractionOrchestratorDependencies dependencies,
        TimeProvider? timeProvider = null,
        Func<string>? attemptIdFactory = null,
        Action<string, string>? moveDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _dependencies = dependencies ??
            throw new ArgumentNullException(nameof(dependencies));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attemptIdFactory = attemptIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        _moveDirectory = moveDirectory ?? Directory.Move;
    }

    public async Task<ExtractionOperationResult> RunAsync(
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);

        GameBuild build;
        ResolvedExtractionProfile profile;
        ResolvedValidationPolicy policy;
        var prefixStage = ExtractionFailureStage.Recovery;
        try
        {
            await _dependencies.InitializeRepositoryAsync(cancellationToken);
            await _dependencies.RecoverAsync(cancellationToken);

            prefixStage = ExtractionFailureStage.InputResolution;
            build = await _dependencies.SelectBuildAsync(
                options.BuildId,
                cancellationToken);
            try
            {
                profile = _dependencies.GetProfile(options.ProfileId);
                policy = _dependencies.GetPolicy(ValidationPolicyId);
            }
            catch (ToolOperationException exception)
            {
                throw MapToolFailure(exception, attemptId: null);
            }
        }
        catch (Exception exception)
        {
            throw MapFailure(exception, prefixStage, attemptId: null);
        }

        return await RunResolvedCoreAsync(
            options,
            build,
            profile,
            policy,
            ct => _dependencies.ResolveToolAsync(options.CustomCpp2IlPath, ct),
            cancellationToken);
    }

    /// <summary>
    /// Runs the Phase 3 Cpp2IL process for an already-prepared extraction context
    /// (build/profile/policy/tool/recipe resolved by
    /// <see cref="ExtractionPreparationService"/>). Unlike <see cref="RunAsync"/>,
    /// it neither initializes the repository, runs recovery, nor re-resolves the
    /// build/profile/policy/tool: the Phase 4 workflow performs those steps once and
    /// only reaches here when no reuse, revalidation, or existing candidate can
    /// satisfy the request. It produces the same durable, non-authoritative
    /// <c>ProcessCompleted</c> candidate <see cref="RunAsync"/> does.
    /// </summary>
    internal Task<ExtractionOperationResult> RunPreparedAsync(
        PreparedExtractionContext context,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);

        return RunResolvedCoreAsync(
            options,
            context.Build,
            context.Profile,
            context.Policy,
            _ => Task.FromResult(context.Tool),
            cancellationToken);
    }

    private async Task<ExtractionOperationResult> RunResolvedCoreAsync(
        ExtractionOptions options,
        GameBuild build,
        ResolvedExtractionProfile profile,
        ResolvedValidationPolicy policy,
        Func<CancellationToken, Task<ResolvedExtractionTool>> resolveToolAsync,
        CancellationToken cancellationToken)
    {
        ExtractionFailureStage stage = ExtractionFailureStage.InputResolution;
        OwnedAttemptPaths? paths = null;
        IExtractionOrchestrationLockLease? lockLease = null;
        ExtractionAttempt? attempt = null;
        ExtractionAttemptStatus persistedStatus = ExtractionAttemptStatus.Created;
        AttemptExecutionFacts? executionFacts = null;
        ResolvedExtractionTool? tool = null;
        ResolvedExtractionInput? input = null;
        InputSnapshot? snapshot = null;
        var databaseAttemptCreated = false;
        var preserveStagingOutput = false;

        try
        {
            var attemptId = _attemptIdFactory();
            paths = OwnedAttemptPaths.Create(_dataRoot, build.BuildId, attemptId);
            try
            {
                lockLease = await _dependencies.AcquireLockAsync(
                    attemptId,
                    cancellationToken);
            }
            catch (ExtractionAlreadyActiveException exception)
            {
                throw new ExtractionOperationException(
                    ExtractionFailureStage.Recovery,
                    ExtractionFailureCode.ExtractionAlreadyActive,
                    exception.Message,
                    innerException: exception);
            }

            attempt = CreateAttempt(
                attemptId,
                build,
                profile,
                policy,
                paths,
                options.KeepFailedArtifacts);
            try
            {
                await _dependencies.AttemptRepository.CreateAttemptAsync(
                    attempt,
                    CancellationToken.None);
                databaseAttemptCreated = true;
            }
            catch (Exception exception)
            {
                throw PersistenceFailure(
                    ExtractionFailureCode.DatabasePromotionFailed,
                    "The extraction attempt could not be created in the repository.",
                    attemptId,
                    exception);
            }

            try
            {
                await _dependencies.WriteAttemptDocumentAsync(
                    paths,
                    attempt,
                    executionFacts,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                throw PersistenceFailure(
                    ExtractionFailureCode.FilesystemPromotionFailed,
                    "The created extraction attempt could not be mirrored to disk.",
                    attemptId,
                    exception);
            }

            stage = ExtractionFailureStage.ToolResolution;
            try
            {
                tool = await resolveToolAsync(cancellationToken);
            }
            catch (ToolOperationException exception)
            {
                throw MapToolFailure(exception, attemptId);
            }

            stage = ExtractionFailureStage.InputResolution;
            input = await _dependencies.ResolveInputAsync(
                build,
                options.GamePath,
                profile.Profile,
                cancellationToken);
            var recipeId = ExtractionRecipeId.Create(new ExtractionRecipe(
                build.BuildId,
                tool.Instance.ToolInstanceId,
                profile.ProfileDigest,
                profile.Profile.AdapterVersion,
                profile.Profile.ExtractionSchemaVersion));
            var arguments = Cpp2IlArgumentBuilder.Build(
                profile.Profile,
                input.GameRoot,
                paths.OutputRoot);
            executionFacts = CreateExecutionFacts(
                _dependencies.OwnerProcessId,
                tool,
                input,
                arguments,
                profile.Profile.Timeout,
                preInputManifest: null,
                postInputManifest: null);
            attempt = attempt with
            {
                RecipeId = recipeId,
                ToolInstanceId = tool.Instance.ToolInstanceId,
                InputSource = input.Source,
                InputSnapshotId = input.InputSnapshotId,
                Status = ExtractionAttemptStatus.Preparing
            };
            await TransitionAsync(
                attempt,
                executionFacts,
                paths,
                persistedStatus,
                cancellationToken: CancellationToken.None,
                databaseTransitioned: () =>
                    persistedStatus = ExtractionAttemptStatus.Preparing);

            stage = ExtractionFailureStage.PreRunInputVerification;
            var preManifest = await _dependencies.CaptureInputManifestAsync(
                input,
                build,
                profile.Profile,
                stage,
                cancellationToken);
            var preDigest = InputManifestFingerprint.Create(preManifest);
            attempt = attempt with { PreInputManifestDigest = preDigest };
            executionFacts = executionFacts with { PreInputManifest = preManifest };
            await WriteActiveDocumentAsync(paths, attempt, executionFacts);

            if (options.SnapshotInputs && input.Source == ExtractionInputSource.Live)
            {
                stage = ExtractionFailureStage.InputSnapshotCreation;
                if (_dependencies.CreateInputSnapshotAsync is null)
                {
                    throw new ExtractionOperationException(
                        stage,
                        ExtractionFailureCode.ArchivedInputInvalid,
                        "Input snapshot creation is not configured.",
                        attemptId);
                }

                try
                {
                    snapshot = await _dependencies.CreateInputSnapshotAsync(
                        input,
                        build,
                        profile.Profile,
                        cancellationToken);
                }
                catch (ExtractionOperationException exception)
                {
                    throw new ExtractionOperationException(
                        stage,
                        exception.Code,
                        exception.Message,
                        attemptId,
                        exception);
                }

                attempt = attempt with { InputSnapshotId = snapshot.InputSnapshotId };
                await WriteActiveDocumentAsync(paths, attempt, executionFacts);
            }

            CreateOwnedExecutionDirectories(paths);
            stage = ExtractionFailureStage.ProcessStart;
            var processRequest = new ExtractionProcessRequest(
                tool.ExecutablePath,
                input.GameRoot,
                paths.WorkingRoot,
                paths.OutputRoot,
                Path.Combine(paths.StagingLogsRoot, "stdout.log"),
                Path.Combine(paths.StagingLogsRoot, "stderr.log"),
                profile);
            ExtractionProcessResult processResult;
            try
            {
                processResult = await _dependencies.ProcessExtractor.ExtractAsync(
                    processRequest,
                    async (processId, _) =>
                    {
                        try
                        {
                            await lockLease.UpdateChildProcessIdAsync(
                                processId,
                                CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            throw PersistenceFailure(
                                ExtractionFailureCode.FilesystemPromotionFailed,
                                "The extraction lock could not record the child process.",
                                attemptId,
                                exception);
                        }

                        attempt = attempt with
                        {
                            Status = ExtractionAttemptStatus.Running,
                            ProcessId = processId,
                            StartedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime()
                        };
                        try
                        {
                            await _dependencies.AttemptRepository.TransitionAttemptAsync(
                                attempt,
                                persistedStatus,
                                CancellationToken.None);
                            persistedStatus = ExtractionAttemptStatus.Running;
                        }
                        catch (Exception exception)
                        {
                            throw PersistenceFailure(
                                ExtractionFailureCode.DatabasePromotionFailed,
                                "The running extraction attempt could not be persisted.",
                                attemptId,
                                exception);
                        }

                        try
                        {
                            await _dependencies.WriteAttemptDocumentAsync(
                                paths,
                                attempt,
                                executionFacts,
                                CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            throw PersistenceFailure(
                                ExtractionFailureCode.FilesystemPromotionFailed,
                                "The running extraction attempt could not be mirrored to disk.",
                                attemptId,
                                exception);
                        }

                        stage = ExtractionFailureStage.ProcessExecution;
                    },
                    cancellationToken);
            }
            catch (Cpp2IlProcessCanceledException exception)
            {
                ApplyCanceledProcessResult(ref attempt, exception);
                throw;
            }
            ApplyProcessResult(ref attempt, processResult);

            if (processResult.TerminationReason ==
                ExtractionProcessTerminationReason.StartFailed)
            {
                throw new ExtractionOperationException(
                    ExtractionFailureStage.ProcessStart,
                    ExtractionFailureCode.ProcessStartFailed,
                    processResult.StartFailureMessage ??
                        "The Cpp2IL process could not be started.",
                    attemptId);
            }

            stage = ExtractionFailureStage.ProcessExecution;
            if (processResult.TerminationReason ==
                ExtractionProcessTerminationReason.TimedOut)
            {
                throw new ExtractionOperationException(
                    stage,
                    ExtractionFailureCode.ProcessTimedOut,
                    "The Cpp2IL process exceeded the profile timeout.",
                    attemptId);
            }

            if (processResult.ExitCode is not int exitCode ||
                !profile.Profile.AcceptedExitCodes.Contains(exitCode))
            {
                throw new ExtractionOperationException(
                    stage,
                    ExtractionFailureCode.ProcessExitNonZero,
                    $"Cpp2IL exited with unaccepted code {processResult.ExitCode?.ToString() ?? "<none>"}.",
                    attemptId);
            }

            stage = ExtractionFailureStage.PostRunInputVerification;
            var postManifest = await _dependencies.CaptureInputManifestAsync(
                input,
                build,
                profile.Profile,
                stage,
                cancellationToken);
            _dependencies.VerifyInputUnchanged(preManifest, postManifest, build);
            var postDigest = InputManifestFingerprint.Create(postManifest);
            attempt = attempt with { PostInputManifestDigest = postDigest };
            executionFacts = executionFacts with { PostInputManifest = postManifest };

            MoveProcessLogs(paths, attempt);
            stage = ExtractionFailureStage.FilesystemPromotion;
            try
            {
                PromoteCandidateOutput(paths);
            }
            catch (Exception exception)
            {
                preserveStagingOutput = true;
                throw new ExtractionOperationException(
                    stage,
                    ExtractionFailureCode.FilesystemPromotionFailed,
                    "The accepted Cpp2IL output could not be promoted to candidate output.",
                    attemptId,
                    exception);
            }

            attempt = attempt with
            {
                Status = ExtractionAttemptStatus.ProcessCompleted,
                CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                CandidateOutputPath = paths.CandidateOutputRoot,
                FailureStage = null,
                FailureCode = null,
                FailureMessage = null,
                ResultExtractionId = null
            };
            await TransitionAsync(
                attempt,
                executionFacts,
                paths,
                persistedStatus,
                CancellationToken.None,
                databaseTransitioned: () =>
                    persistedStatus = ExtractionAttemptStatus.ProcessCompleted);

            return new ExtractionOperationResult(
                attempt,
                tool.Instance,
                input.Source,
                snapshot?.InputSnapshotId ?? input.InputSnapshotId,
                ProcessWasRun: true,
                IsAuthoritative: false);
        }
        catch (Exception exception)
        {
            var mapped = MapFailure(exception, stage, attempt?.AttemptId);
            if (attempt is not null && paths is not null && databaseAttemptCreated)
            {
                var terminal = await FinalizeTerminalFailureAsync(
                    attempt,
                    executionFacts,
                    paths,
                    persistedStatus,
                    mapped,
                    options.KeepFailedArtifacts,
                    preserveStagingOutput);
                attempt = terminal.Attempt;
                mapped = terminal.Failure;
            }

            throw mapped;
        }
        finally
        {
            if (lockLease is not null)
            {
                await lockLease.ReleaseAsync(CancellationToken.None);
            }

            if (paths is not null)
            {
                DeleteOnlyEmptyOwnedStaging(paths);
            }
        }
    }

    private static ExtractionOrchestratorDependencies CreateDependencies(
        IAtlasRepository atlasRepository,
        IExtractionRepository extractionRepository,
        IExtractionProfileProvider profileProvider,
        IValidationPolicyProvider validationPolicyProvider,
        ExtractionToolResolver toolResolver,
        ExtractionInputResolver inputResolver,
        LiveInputVerifier inputVerifier,
        Func<GameBuild, InputSnapshotService>? inputSnapshotServiceFactory,
        AttemptDocumentStore attemptDocumentStore,
        ExtractionLock extractionLock,
        ExtractionRecoveryService recoveryService,
        IIl2CppExtractor processExtractor)
    {
        ArgumentNullException.ThrowIfNull(atlasRepository);
        ArgumentNullException.ThrowIfNull(extractionRepository);
        ArgumentNullException.ThrowIfNull(profileProvider);
        ArgumentNullException.ThrowIfNull(validationPolicyProvider);
        ArgumentNullException.ThrowIfNull(toolResolver);
        ArgumentNullException.ThrowIfNull(inputResolver);
        ArgumentNullException.ThrowIfNull(inputVerifier);
        ArgumentNullException.ThrowIfNull(attemptDocumentStore);
        ArgumentNullException.ThrowIfNull(extractionLock);
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(processExtractor);

        return new ExtractionOrchestratorDependencies
        {
            InitializeRepositoryAsync = atlasRepository.InitializeAsync,
            RecoverAsync = recoveryService.RecoverAsync,
            SelectBuildAsync = inputResolver.SelectBuildAsync,
            GetProfile = profileProvider.GetRequired,
            GetPolicy = validationPolicyProvider.GetRequired,
            AcquireLockAsync = async (attemptId, cancellationToken) =>
                new ExtractionOrchestrationLockLease(
                    await extractionLock.AcquireAsync(attemptId, cancellationToken)),
            AttemptRepository = extractionRepository,
            ResolveToolAsync = toolResolver.ResolveAsync,
            ResolveInputAsync = inputResolver.ResolveAsync,
            CaptureInputManifestAsync = (
                input,
                build,
                profile,
                stage,
                cancellationToken) => inputVerifier.CaptureAsync(
                    input,
                    build,
                    profile,
                    cancellationToken,
                    stage),
            VerifyInputUnchanged = inputVerifier.VerifyUnchanged,
            CreateInputSnapshotAsync = inputSnapshotServiceFactory is null
                ? null
                : (input, build, profile, cancellationToken) =>
                    inputSnapshotServiceFactory(build).CreateAsync(
                        input,
                        build,
                        profile,
                        cancellationToken),
            WriteAttemptDocumentAsync = attemptDocumentStore.WriteAsync,
            ProcessExtractor = processExtractor
        };
    }

    private ExtractionAttempt CreateAttempt(
        string attemptId,
        GameBuild build,
        ResolvedExtractionProfile profile,
        ResolvedValidationPolicy policy,
        OwnedAttemptPaths paths,
        bool keepFailedArtifacts) => new(
            attemptId,
            RecipeId: null,
            build.BuildId,
            ToolInstanceId: null,
            profile.Profile.ProfileId,
            profile.Profile.ProfileVersion,
            profile.ProfileDigest,
            policy.Policy.PolicyId,
            policy.Policy.PolicyVersion,
            policy.PolicyDigest,
            profile.Profile.AdapterVersion,
            profile.Profile.ExtractionSchemaVersion,
            InputSource: null,
            InputSnapshotId: null,
            ExtractionAttemptStatus.Created,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            StartedAtUtc: null,
            CompletedAtUtc: null,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            paths.WorkingRoot,
            Path.Combine(paths.FinalLogsRoot, "stdout.log"),
            Path.Combine(paths.FinalLogsRoot, "stderr.log"),
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: null,
            ProcessExitCode: null,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            keepFailedArtifacts,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: null,
            ResultExtractionId: null);

    private static AttemptExecutionFacts CreateExecutionFacts(
        int ownerProcessId,
        ResolvedExtractionTool tool,
        ResolvedExtractionInput input,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        InputManifest? preInputManifest,
        InputManifest? postInputManifest) => new(
            ownerProcessId,
            tool.Instance.TrustLevel.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gameRoot"] = input.GameRoot,
                ["gameAssembly"] = input.GameAssemblyPath,
                ["globalMetadata"] = input.GlobalMetadataPath,
                ["executableSupport"] = input.ExecutablePath,
                ["unityVersionSource"] = input.UnityVersionSourcePath
            },
            arguments,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["NO_COLOR"] = "true"
            },
            preInputManifest,
            postInputManifest,
            timeout);

    private async Task TransitionAsync(
        ExtractionAttempt next,
        AttemptExecutionFacts? executionFacts,
        OwnedAttemptPaths paths,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken,
        Action? databaseTransitioned = null)
    {
        try
        {
            await _dependencies.AttemptRepository.TransitionAttemptAsync(
                next,
                expectedStatus,
                cancellationToken);
            databaseTransitioned?.Invoke();
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw PersistenceFailure(
                ExtractionFailureCode.DatabasePromotionFailed,
                $"Extraction attempt '{next.AttemptId}' could not transition to {next.Status}.",
                next.AttemptId,
                exception);
        }

        try
        {
            await _dependencies.WriteAttemptDocumentAsync(
                paths,
                next,
                executionFacts,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw PersistenceFailure(
                ExtractionFailureCode.FilesystemPromotionFailed,
                $"Extraction attempt '{next.AttemptId}' could not mirror {next.Status} to disk.",
                next.AttemptId,
                exception);
        }
    }

    private async Task WriteActiveDocumentAsync(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt,
        AttemptExecutionFacts executionFacts)
    {
        try
        {
            await _dependencies.WriteAttemptDocumentAsync(
                paths,
                attempt,
                executionFacts,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw PersistenceFailure(
                ExtractionFailureCode.FilesystemPromotionFailed,
                "The active extraction attempt could not be mirrored to disk.",
                attempt.AttemptId,
                exception);
        }
    }

    private static void CreateOwnedExecutionDirectories(OwnedAttemptPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.WorkingRoot);
            Directory.CreateDirectory(paths.OutputRoot);
            Directory.CreateDirectory(paths.StagingLogsRoot);
            if (!PathSafety.IsNormalDirectory(paths.StagingRoot) ||
                !PathSafety.IsNormalDirectory(paths.WorkingRoot) ||
                !PathSafety.IsNormalDirectory(paths.OutputRoot) ||
                !PathSafety.IsNormalDirectory(paths.StagingLogsRoot))
            {
                throw new InvalidOperationException(
                    "An extraction staging directory is unsafe.");
            }
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.ProcessStart,
                ExtractionFailureCode.ProcessStartFailed,
                "Atlas-owned Cpp2IL staging directories could not be prepared.",
                paths.AttemptId,
                exception);
        }
    }

    private static void ApplyProcessResult(
        ref ExtractionAttempt attempt,
        ExtractionProcessResult result)
    {
        attempt = attempt with
        {
            ProcessId = result.ProcessId ?? attempt.ProcessId,
            ProcessExitCode = result.ExitCode,
            StartedAtUtc = result.StartedAtUtc ?? attempt.StartedAtUtc,
            StandardOutputTruncated = result.StandardOutput.Truncated,
            StandardErrorTruncated = result.StandardError.Truncated,
            StandardOutputDiscardedBytes = result.StandardOutput.DiscardedBytes,
            StandardErrorDiscardedBytes = result.StandardError.DiscardedBytes
        };
    }

    private static void ApplyCanceledProcessResult(
        ref ExtractionAttempt attempt,
        Cpp2IlProcessCanceledException exception)
    {
        attempt = attempt with
        {
            ProcessId = exception.ProcessId ?? attempt.ProcessId,
            ProcessExitCode = null,
            StartedAtUtc = exception.StartedAtUtc ?? attempt.StartedAtUtc,
            StandardOutputTruncated = exception.StandardOutput.Truncated,
            StandardErrorTruncated = exception.StandardError.Truncated,
            StandardOutputDiscardedBytes = exception.StandardOutput.DiscardedBytes,
            StandardErrorDiscardedBytes = exception.StandardError.DiscardedBytes
        };
    }

    private void MoveProcessLogs(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        EnsureSafeStaging(paths);
        Directory.CreateDirectory(paths.FinalLogsRoot);
        if (!PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The final extraction log directory is unsafe.");
        }

        MoveExactLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stdout.log"),
            attempt.StandardOutputPath);
        MoveExactLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stderr.log"),
            attempt.StandardErrorPath);
        DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
    }

    private void PromoteCandidateOutput(OwnedAttemptPaths paths)
    {
        EnsureSafeStaging(paths);
        var output = InspectOwnedOutput(paths);
        _ = output;
        if (OwnedAttemptPaths.EntryExists(paths.CandidateOutputRoot))
        {
            throw new IOException("Candidate output already exists.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            paths.CandidateOutputRoot);
        _moveDirectory(paths.OutputRoot, paths.CandidateOutputRoot);
    }

    private async Task<TerminalFailureResult> FinalizeTerminalFailureAsync(
        ExtractionAttempt attempt,
        AttemptExecutionFacts? executionFacts,
        OwnedAttemptPaths paths,
        ExtractionAttemptStatus persistedStatus,
        ExtractionOperationException failure,
        bool keepFailedArtifacts,
        bool preserveStagingOutput)
    {
        FinalizeFailureLogs(paths, attempt);
        var discarded = new OutputFacts(0, 0);
        var effectiveFailure = failure;
        if (!preserveStagingOutput && Directory.Exists(paths.OutputRoot))
        {
            try
            {
                discarded = InspectOwnedOutput(paths);
                if (keepFailedArtifacts)
                {
                    if (OwnedAttemptPaths.EntryExists(paths.RetainedOutputRoot))
                    {
                        throw FilesystemFailure(
                            paths.AttemptId,
                            "Retained output already exists.");
                    }

                    OwnedAttemptPaths.EnsureSafeExistingPath(
                        paths.AttemptRoot,
                        paths.RetainedOutputRoot);
                    _moveDirectory(paths.OutputRoot, paths.RetainedOutputRoot);
                    discarded = new OutputFacts(0, 0);
                }
                else
                {
                    Directory.Delete(paths.OutputRoot, recursive: true);
                }
            }
            catch (Exception exception)
            {
                discarded = new OutputFacts(0, 0);
                effectiveFailure = exception as ExtractionOperationException ??
                    FilesystemFailure(
                        paths.AttemptId,
                        "Failed Cpp2IL output could not be safely finalized.",
                        exception);
            }
        }

        var canceled = effectiveFailure.Code == ExtractionFailureCode.OperationCanceled;
        var terminal = attempt with
        {
            Status = canceled
                ? ExtractionAttemptStatus.Canceled
                : ExtractionAttemptStatus.Failed,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            FailureStage = canceled ? null : effectiveFailure.Stage,
            FailureCode = canceled ? null : effectiveFailure.Code,
            FailureMessage = canceled ? null : effectiveFailure.Message,
            KeepFailedArtifacts = keepFailedArtifacts,
            DiscardedFileCount = checked(attempt.DiscardedFileCount + discarded.FileCount),
            DiscardedByteCount = checked(attempt.DiscardedByteCount + discarded.ByteCount),
            CandidateOutputPath = null,
            ResultExtractionId = null
        };
        await TransitionAsync(
            terminal,
            executionFacts,
            paths,
            persistedStatus,
            CancellationToken.None);
        return new TerminalFailureResult(terminal, effectiveFailure);
    }

    private void FinalizeFailureLogs(
        OwnedAttemptPaths paths,
        ExtractionAttempt attempt)
    {
        Directory.CreateDirectory(paths.FinalLogsRoot);
        if (!PathSafety.IsNormalDirectory(paths.FinalLogsRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The final extraction log directory is unsafe.");
        }

        FinalizeOneFailureLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stdout.log"),
            attempt.StandardOutputPath);
        FinalizeOneFailureLog(
            paths,
            Path.Combine(paths.StagingLogsRoot, "stderr.log"),
            attempt.StandardErrorPath);
        DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
    }

    private void FinalizeOneFailureLog(
        OwnedAttemptPaths paths,
        string stagingPath,
        string finalPath)
    {
        if (File.Exists(stagingPath))
        {
            MoveExactLog(paths, stagingPath, finalPath);
            return;
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            finalPath,
            allowFinalFile: true);
        if (!File.Exists(finalPath))
        {
            using var stream = new FileStream(
                finalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
    }

    private void MoveExactLog(
        OwnedAttemptPaths paths,
        string source,
        string destination)
    {
        if (!PathSafety.TryObserveRegularFile(paths.StagingLogsRoot, source, out _))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                $"Expected staging log '{Path.GetFileName(source)}' is missing or unsafe.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            paths.AttemptRoot,
            destination,
            allowFinalFile: true);
        if (File.Exists(destination))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                $"Final log '{Path.GetFileName(destination)}' already exists.");
        }

        File.Move(source, destination);
    }

    private static OutputFacts InspectOwnedOutput(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.OutputRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The owned Cpp2IL output directory is missing.");
        }

        EnsureSafeStaging(paths);
        if (!PathSafety.IsNormalDirectory(paths.OutputRoot) ||
            !OwnedAttemptPaths.IsSameOrDescendant(
                paths.StagingRoot,
                paths.OutputRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The owned Cpp2IL output directory is unsafe.");
        }

        var fileCount = 0;
        long byteCount = 0;
        var pending = new Stack<string>();
        pending.Push(paths.OutputRoot);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw FilesystemFailure(
                        paths.AttemptId,
                        "Cpp2IL output contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    fileCount = checked(fileCount + 1);
                    byteCount = checked(byteCount + new FileInfo(entry).Length);
                }
            }
        }

        return new OutputFacts(fileCount, byteCount);
    }

    private static void EnsureSafeStaging(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingRoot) ||
            !PathSafety.IsNormalDirectory(paths.StagingRoot) ||
            !OwnedAttemptPaths.PathsEqualParent(paths.StagingRoot, paths.AttemptId))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The extraction staging directory is missing or unsafe.");
        }
    }

    private static void DeleteOnlyEmptyOwnedStaging(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingRoot))
        {
            return;
        }

        try
        {
            EnsureSafeStaging(paths);
            DeleteDirectoryIfEmpty(paths.WorkingRoot);
            DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
            DeleteDirectoryIfEmpty(paths.OutputRoot);
            DeleteDirectoryIfEmpty(paths.StagingRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                ExtractionOperationException)
        {
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static ExtractionOperationException MapFailure(
        Exception exception,
        ExtractionFailureStage currentStage,
        string? attemptId)
    {
        if (exception is ExtractionOperationException extractionFailure)
        {
            return extractionFailure.AttemptId is null && attemptId is not null
                ? new ExtractionOperationException(
                    extractionFailure.Stage,
                    extractionFailure.Code,
                    extractionFailure.Message,
                    attemptId,
                    extractionFailure)
                : extractionFailure;
        }

        if (exception is ToolOperationException toolFailure)
        {
            return MapToolFailure(toolFailure, attemptId);
        }

        if (exception is OperationCanceledException)
        {
            return new ExtractionOperationException(
                currentStage,
                ExtractionFailureCode.OperationCanceled,
                "The extraction operation was canceled.",
                attemptId,
                exception);
        }

        var code = currentStage switch
        {
            ExtractionFailureStage.ProcessStart => ExtractionFailureCode.ProcessStartFailed,
            ExtractionFailureStage.PostRunInputVerification =>
                ExtractionFailureCode.InputChangedDuringExtraction,
            ExtractionFailureStage.InputSnapshotCreation =>
                ExtractionFailureCode.FilesystemPromotionFailed,
            ExtractionFailureStage.FilesystemPromotion =>
                ExtractionFailureCode.FilesystemPromotionFailed,
            _ => ExtractionFailureCode.IntegrityMismatch
        };
        return new ExtractionOperationException(
            currentStage,
            code,
            "The extraction operation failed.",
            attemptId,
            exception);
    }

    private static ExtractionOperationException MapToolFailure(
        ToolOperationException exception,
        string? attemptId)
    {
        var code = Enum.TryParse<ExtractionFailureCode>(
            exception.Code,
            ignoreCase: false,
            out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : ExtractionFailureCode.ToolDefinitionInvalid;
        return new ExtractionOperationException(
            ExtractionFailureStage.ToolResolution,
            code,
            exception.Message,
            attemptId,
            exception);
    }

    private static ExtractionOperationException PersistenceFailure(
        ExtractionFailureCode code,
        string message,
        string attemptId,
        Exception exception) => new(
            ExtractionFailureStage.AttemptPersistence,
            code,
            message,
            attemptId,
            exception);

    private static ExtractionOperationException FilesystemFailure(
        string attemptId,
        string message,
        Exception? innerException = null) => new(
            ExtractionFailureStage.FilesystemPromotion,
            ExtractionFailureCode.FilesystemPromotionFailed,
            message,
            attemptId,
            innerException);

    private readonly record struct OutputFacts(int FileCount, long ByteCount);

    private sealed record TerminalFailureResult(
        ExtractionAttempt Attempt,
        ExtractionOperationException Failure);
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
