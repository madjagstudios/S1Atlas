using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

public sealed partial class ExtractionOrchestrator
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
                options.InputSnapshotId,
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
}
