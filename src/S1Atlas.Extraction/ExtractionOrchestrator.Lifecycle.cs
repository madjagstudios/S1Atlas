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
}
