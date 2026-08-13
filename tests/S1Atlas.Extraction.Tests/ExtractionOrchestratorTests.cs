using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tests.Inputs;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests;

public sealed class ExtractionOrchestratorTests
{
    public static TheoryData<FailurePoint, ExtractionAttemptStatus, ExtractionFailureStage, ExtractionFailureCode>
        TerminalFailures => new()
        {
            { FailurePoint.ManagedToolAbsent, ExtractionAttemptStatus.Failed, ExtractionFailureStage.ToolResolution, ExtractionFailureCode.ToolNotInstalled },
            { FailurePoint.CustomProbeFails, ExtractionAttemptStatus.Failed, ExtractionFailureStage.ToolResolution, ExtractionFailureCode.ToolProbeFailed },
            { FailurePoint.LiveInputAbsent, ExtractionAttemptStatus.Failed, ExtractionFailureStage.InputResolution, ExtractionFailureCode.LiveInputNotFound },
            { FailurePoint.PreRunMismatch, ExtractionAttemptStatus.Failed, ExtractionFailureStage.PreRunInputVerification, ExtractionFailureCode.BuildInputMismatch },
            { FailurePoint.SnapshotInvalid, ExtractionAttemptStatus.Failed, ExtractionFailureStage.InputSnapshotCreation, ExtractionFailureCode.ArchivedInputInvalid },
            { FailurePoint.SnapshotFilesystemPromotion, ExtractionAttemptStatus.Failed, ExtractionFailureStage.InputSnapshotCreation, ExtractionFailureCode.FilesystemPromotionFailed },
            { FailurePoint.SnapshotDatabasePersistence, ExtractionAttemptStatus.Failed, ExtractionFailureStage.InputSnapshotCreation, ExtractionFailureCode.DatabasePromotionFailed },
            { FailurePoint.ProcessStart, ExtractionAttemptStatus.Failed, ExtractionFailureStage.ProcessStart, ExtractionFailureCode.ProcessStartFailed },
            { FailurePoint.ProcessTimeout, ExtractionAttemptStatus.Failed, ExtractionFailureStage.ProcessExecution, ExtractionFailureCode.ProcessTimedOut },
            { FailurePoint.ProcessExitNonzero, ExtractionAttemptStatus.Failed, ExtractionFailureStage.ProcessExecution, ExtractionFailureCode.ProcessExitNonZero },
            { FailurePoint.PostRunMutation, ExtractionAttemptStatus.Failed, ExtractionFailureStage.PostRunInputVerification, ExtractionFailureCode.InputChangedDuringExtraction },
            { FailurePoint.CallerCancellation, ExtractionAttemptStatus.Canceled, ExtractionFailureStage.ProcessExecution, ExtractionFailureCode.OperationCanceled },
            { FailurePoint.ProcessStartDatabasePersistence, ExtractionAttemptStatus.Failed, ExtractionFailureStage.AttemptPersistence, ExtractionFailureCode.DatabasePromotionFailed },
            { FailurePoint.PreparingDocumentMirror, ExtractionAttemptStatus.Failed, ExtractionFailureStage.AttemptPersistence, ExtractionFailureCode.FilesystemPromotionFailed },
            { FailurePoint.ProcessStartDocumentMirror, ExtractionAttemptStatus.Failed, ExtractionFailureStage.AttemptPersistence, ExtractionFailureCode.FilesystemPromotionFailed },
            { FailurePoint.CandidateMove, ExtractionAttemptStatus.Failed, ExtractionFailureStage.FilesystemPromotion, ExtractionFailureCode.FilesystemPromotionFailed }
        };

    [Fact]
    public async Task RunAsync_AcceptedProcessEndsAsDurableNonAuthoritativeCandidate()
    {
        using var fixture = new OrchestratorFixture();

        var result = await fixture.CreateOrchestrator().RunAsync(
            fixture.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ExtractionAttemptStatus.Created,
                ExtractionAttemptStatus.Preparing,
                ExtractionAttemptStatus.Running,
                ExtractionAttemptStatus.ProcessCompleted
            ],
            fixture.Repository.RecordedStatuses);
        Assert.Equal(ExtractionAttemptStatus.ProcessCompleted, result.Attempt.Status);
        Assert.Null(result.Attempt.ResultExtractionId);
        Assert.Null(result.Attempt.FailureCode);
        Assert.NotNull(result.Attempt.CandidateOutputPath);
        Assert.True(Directory.Exists(result.Attempt.CandidateOutputPath));
        Assert.False(File.Exists(Path.Combine(
            result.Attempt.CandidateOutputPath,
            "complete.marker")));
        Assert.False(result.IsAuthoritative);
        Assert.True(result.ProcessWasRun);
        Assert.Equal(fixture.Tool.Instance, result.ToolInstance);
        Assert.Equal(ExtractionInputSource.Live, result.InputSource);
        Assert.Null(result.InputSnapshotId);
        Assert.Equal(result.Attempt.PreInputManifestDigest, result.Attempt.PostInputManifestDigest);
        Assert.False(File.Exists(Path.Combine(fixture.AtlasRoot, "extraction.lock")));
        Assert.False(Directory.Exists(fixture.Paths.StagingRoot));
        Assert.True(File.Exists(fixture.Paths.AttemptDocumentPath));
        Assert.True(File.Exists(result.Attempt.StandardOutputPath));
        Assert.True(File.Exists(result.Attempt.StandardErrorPath));
        Assert.True(IsContained(fixture.AtlasRoot, result.Attempt.StandardOutputPath));
        Assert.True(IsContained(fixture.AtlasRoot, result.Attempt.StandardErrorPath));
        Assert.True(IsContained(fixture.AtlasRoot, result.Attempt.CandidateOutputPath));
        Assert.True(fixture.Lock.Released);

        var document = await new AttemptDocumentStore().TryReadAsync(
            fixture.Paths,
            result.Attempt,
            TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.NotNull(document.ExecutionFacts);
        Assert.Equal(fixture.ExpectedArguments, document.ExecutionFacts.Arguments);
        Assert.Equal(
            new Dictionary<string, string?> { ["NO_COLOR"] = "true" },
            document.ExecutionFacts.EnvironmentOverrides);
        Assert.Equal(fixture.Tool.Instance.TrustLevel.ToString(), document.ExecutionFacts.ToolTrust);
        Assert.Equal(fixture.Profile.Profile.ProfileId, result.Attempt.ProfileId);
        Assert.Equal(fixture.Profile.ProfileDigest, result.Attempt.ProfileDigest);
        Assert.Equal(fixture.Policy.Policy.PolicyId, result.Attempt.ValidationPolicyId);
        Assert.Equal(fixture.Policy.PolicyDigest, result.Attempt.ValidationPolicyDigest);
        Assert.Equal(
            ExtractionRecipeId.Create(new ExtractionRecipe(
                fixture.Build.BuildId,
                fixture.Tool.Instance.ToolInstanceId,
                fixture.Profile.ProfileDigest,
                fixture.Profile.Profile.AdapterVersion,
                fixture.Profile.Profile.ExtractionSchemaVersion)),
            result.Attempt.RecipeId);
        Assert.DoesNotContain(
            typeof(IExtractionRepository).GetMethods(),
            method => method.Name.Contains("Validated", StringComparison.Ordinal) ||
                method.Name.Contains("Preference", StringComparison.Ordinal));
        Assert.Equal(
            [
                "initialize", "recover", "select-build", "load-profile", "load-policy",
                "acquire-lock", "created", "resolve-tool", "resolve-input", "preparing",
                "capture-pre", "process-start", "lock-child", "running", "capture-post",
                "process-completed", "release-lock"
            ],
            fixture.Events);
    }

    [Fact]
    public async Task RunAsync_RetryShapeStillRunsBecausePhase3HasNoReusableValidatedResult()
    {
        using var fixture = new OrchestratorFixture();

        var result = await fixture.CreateOrchestrator().RunAsync(
            fixture.Options with { Retry = true },
            TestContext.Current.CancellationToken);

        Assert.True(result.ProcessWasRun);
        Assert.Equal(ExtractionAttemptStatus.ProcessCompleted, result.Attempt.Status);
        Assert.Contains("process-start", fixture.Events);
    }

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public async Task RunAsync_FailureOrCancellationMapsExactlyAndKeepsDurableDiagnostics(
        FailurePoint failurePoint,
        ExtractionAttemptStatus expectedStatus,
        ExtractionFailureStage expectedStage,
        ExtractionFailureCode expectedCode)
    {
        using var fixture = new OrchestratorFixture(failurePoint);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options with
                {
                    SnapshotInputs = failurePoint is
                        FailurePoint.SnapshotInvalid or
                        FailurePoint.SnapshotFilesystemPromotion or
                        FailurePoint.SnapshotDatabasePersistence
                },
                TestContext.Current.CancellationToken));

        var attempt = Assert.Single(fixture.Repository.Attempts);
        Assert.Equal(fixture.AttemptId, exception.AttemptId);
        Assert.Equal(expectedStage, exception.Stage);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedStatus, attempt.Status);
        if (expectedStatus == ExtractionAttemptStatus.Failed)
        {
            Assert.Equal(expectedStage, attempt.FailureStage);
            Assert.Equal(expectedCode, attempt.FailureCode);
            Assert.False(string.IsNullOrWhiteSpace(attempt.FailureMessage));
        }
        else
        {
            Assert.Null(attempt.FailureStage);
            Assert.Null(attempt.FailureCode);
            Assert.Null(attempt.FailureMessage);
            Assert.Equal(7, attempt.StandardOutputDiscardedBytes);
            Assert.Equal(11, attempt.StandardErrorDiscardedBytes);
            Assert.True(attempt.StandardOutputTruncated);
            Assert.True(attempt.StandardErrorTruncated);
        }

        Assert.Null(attempt.CandidateOutputPath);
        Assert.False(Directory.Exists(fixture.Paths.CandidateOutputRoot));
        Assert.True(File.Exists(fixture.Paths.AttemptDocumentPath));
        Assert.True(File.Exists(attempt.StandardOutputPath));
        Assert.True(File.Exists(attempt.StandardErrorPath));
        Assert.True(fixture.Lock.Released);
        Assert.True(Directory.Exists(fixture.UnownedStagingSibling));

        var document = await new AttemptDocumentStore().TryReadAsync(
            fixture.Paths,
            attempt,
            TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal(expectedStatus, document.Attempt.Status);

        if (failurePoint == FailurePoint.CandidateMove)
        {
            Assert.True(Directory.Exists(fixture.Paths.OutputRoot));
            Assert.True(Directory.Exists(fixture.Paths.StagingRoot));
        }
        else
        {
            Assert.False(Directory.Exists(fixture.Paths.OutputRoot));
        }

        if (failurePoint == FailurePoint.SnapshotDatabasePersistence)
        {
            Assert.True(Directory.Exists(fixture.Snapshot.RootPath));
            Assert.True(File.Exists(Path.Combine(fixture.Snapshot.RootPath, "snapshot.bin")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ProcessFailureCountsOrRetainsOnlyOwnedOutput(bool keep)
    {
        using var fixture = new OrchestratorFixture(FailurePoint.ProcessTimeout);

        await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options with { KeepFailedArtifacts = keep },
                TestContext.Current.CancellationToken));

        var attempt = Assert.Single(fixture.Repository.Attempts);
        if (keep)
        {
            Assert.True(Directory.Exists(fixture.Paths.RetainedOutputRoot));
            Assert.True(File.Exists(Path.Combine(
                fixture.Paths.RetainedOutputRoot,
                "partial.bin")));
            Assert.Equal(0, attempt.DiscardedFileCount);
            Assert.Equal(0, attempt.DiscardedByteCount);
        }
        else
        {
            Assert.False(Directory.Exists(fixture.Paths.RetainedOutputRoot));
            Assert.Equal(1, attempt.DiscardedFileCount);
            Assert.Equal(5, attempt.DiscardedByteCount);
        }
    }

    [Fact]
    public async Task RunAsync_RequestedSnapshotSurvivesLaterProcessFailure()
    {
        using var fixture = new OrchestratorFixture(FailurePoint.ProcessTimeout);

        await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options with { SnapshotInputs = true },
                TestContext.Current.CancellationToken));

        var attempt = Assert.Single(fixture.Repository.Attempts);
        Assert.Equal(fixture.Snapshot.InputSnapshotId, attempt.InputSnapshotId);
        Assert.True(Directory.Exists(fixture.Snapshot.RootPath));
        Assert.True(File.Exists(Path.Combine(fixture.Snapshot.RootPath, "snapshot.bin")));
    }

    [Fact]
    public async Task RunAsync_WhenFailureRetentionMoveFails_PreservesStagingAndPersistsPromotionFailure()
    {
        using var fixture = new OrchestratorFixture(FailurePoint.RetentionMove);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options with { KeepFailedArtifacts = true },
                TestContext.Current.CancellationToken));

        var attempt = Assert.Single(fixture.Repository.Attempts);
        Assert.Equal(ExtractionFailureStage.FilesystemPromotion, exception.Stage);
        Assert.Equal(ExtractionFailureCode.FilesystemPromotionFailed, exception.Code);
        Assert.Equal(ExtractionAttemptStatus.Failed, attempt.Status);
        Assert.Equal(ExtractionFailureStage.FilesystemPromotion, attempt.FailureStage);
        Assert.Equal(ExtractionFailureCode.FilesystemPromotionFailed, attempt.FailureCode);
        Assert.True(Directory.Exists(fixture.Paths.OutputRoot));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.OutputRoot, "partial.bin")));
        Assert.True(File.Exists(fixture.Paths.AttemptDocumentPath));
        Assert.True(File.Exists(attempt.StandardOutputPath));
        Assert.True(File.Exists(attempt.StandardErrorPath));
        Assert.True(fixture.Lock.Released);
    }

    [Fact]
    public async Task RunAsync_LaterFailureNeverModifiesEarlierTerminalCandidate()
    {
        using var fixture = new OrchestratorFixture();
        var first = await fixture.CreateOrchestrator().RunAsync(
            fixture.Options,
            TestContext.Current.CancellationToken);
        var candidate = first.Attempt.CandidateOutputPath!;
        var originalBytes = await File.ReadAllBytesAsync(
            Path.Combine(candidate, "candidate.bin"),
            TestContext.Current.CancellationToken);
        fixture.BeginSecondAttempt(FailurePoint.ProcessExitNonzero);

        await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options,
                TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(candidate));
        Assert.Equal(
            originalBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(candidate, "candidate.bin"),
                TestContext.Current.CancellationToken));
        Assert.Equal(ExtractionAttemptStatus.ProcessCompleted, fixture.Repository.Attempts[0].Status);
        Assert.Equal(ExtractionAttemptStatus.Failed, fixture.Repository.Attempts[1].Status);
    }

    [Fact]
    public async Task RunAsync_WhenBuildSelectionFails_ReturnsStructuredErrorWithoutInventedAttempt()
    {
        using var fixture = new OrchestratorFixture(FailurePoint.BuildSelection);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options,
                TestContext.Current.CancellationToken));

        Assert.Null(exception.AttemptId);
        Assert.Equal(ExtractionFailureCode.BuildNotFound, exception.Code);
        Assert.Empty(fixture.Repository.Attempts);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.AtlasRoot,
            "builds",
            fixture.Build.BuildId,
            "attempts")));
    }

    [Fact]
    [Trait("Name", "Cancel")]
    public async Task RunAsync_WhenCallerCancelsRunningProcess_ReleasesLockAndPreservesUnownedSibling()
    {
        using var fixture = new OrchestratorFixture(FailurePoint.CallerCancellation);

        await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateOrchestrator().RunAsync(
                fixture.Options,
                TestContext.Current.CancellationToken));

        Assert.True(fixture.Lock.Released);
        Assert.True(Directory.Exists(fixture.UnownedStagingSibling));
        Assert.False(File.Exists(Path.Combine(fixture.AtlasRoot, "extraction.lock")));
        Assert.Equal(ExtractionAttemptStatus.Canceled, Assert.Single(fixture.Repository.Attempts).Status);
    }

    public enum FailurePoint
    {
        None,
        BuildSelection,
        ManagedToolAbsent,
        CustomProbeFails,
        LiveInputAbsent,
        PreRunMismatch,
        SnapshotInvalid,
        SnapshotFilesystemPromotion,
        SnapshotDatabasePersistence,
        ProcessStart,
        ProcessTimeout,
        ProcessExitNonzero,
        PostRunMutation,
        CallerCancellation,
        ProcessStartDatabasePersistence,
        PreparingDocumentMirror,
        ProcessStartDocumentMirror,
        CandidateMove,
        RetentionMove
    }

    private sealed class OrchestratorFixture : IDisposable
    {
        private readonly InputTestFixture _input = InputTestFixture.Create();
        private readonly AttemptDocumentStore _documentStore = new();
        private FailurePoint _failurePoint;
        private bool _documentFailureInjected;
        private int _captureCount;
        private int _attemptNumber = 1;

        public OrchestratorFixture(FailurePoint failurePoint = FailurePoint.None)
        {
            _failurePoint = failurePoint;
            AtlasRoot = Path.Combine(
                Path.GetTempPath(),
                $"s1atlas-orchestrator-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(AtlasRoot);
            AttemptId = Guid.NewGuid().ToString("N");
            Build = _input.Build with { BuildId = new string('0', 64) };
            Profile = new ResolvedExtractionProfile(
                InputTestFixture.Profile,
                new string('a', 64));
            Policy = new ResolvedValidationPolicy(
                new ValidationPolicy(
                    1,
                    "managed-assemblies-v1",
                    1,
                    ["Assembly-CSharp"],
                    1,
                    1,
                    1,
                    1,
                    0.25,
                    0.80),
                new string('b', 64));
            Tool = new ResolvedExtractionTool(
                Definition: null!,
                new ToolInstance(
                    new string('c', 64),
                    "cpp2il",
                    null,
                    "win-x64",
                    ToolTrustLevel.CustomOverride,
                    null,
                    null,
                    new string('d', 64),
                    Path.Combine(AtlasRoot, "fake-cpp2il.exe"),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    ToolInstallationStatus.Verified),
                Path.Combine(AtlasRoot, "fake-cpp2il.exe"),
                []);
            Input = new ResolvedExtractionInput(
                ExtractionInputSource.Live,
                _input.RootPath,
                _input.GameAssemblyPath,
                _input.MetadataPath,
                _input.ExecutablePath,
                _input.UnityVersionPath,
                null);
            Manifest = new InputManifest(
            [
                new("GameAssembly.dll", "gameAssembly", 4, Build.GameAssemblySha256, DateTimeOffset.UtcNow),
                new("Schedule I.exe", "executableSupport", 2, new string('e', 64), DateTimeOffset.UtcNow),
                new("Schedule I_Data/globalgamemanagers", "unityVersionSource", 1, new string('f', 64), DateTimeOffset.UtcNow),
                new("Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat", "globalMetadata", 3, Build.MetadataSha256, DateTimeOffset.UtcNow)
            ]);
            Snapshot = CreateSnapshot();
            Repository = new RecordingRepository(Events, () => _failurePoint);
            Lock = new RecordingLockLease(Events);
            UnownedStagingSibling = Path.Combine(
                AtlasRoot,
                "builds",
                Build.BuildId,
                "extractions",
                ".staging",
                "unowned-sibling");
            Directory.CreateDirectory(UnownedStagingSibling);
            File.WriteAllText(Path.Combine(UnownedStagingSibling, "keep.txt"), "keep");
            Paths = OwnedAttemptPaths.Create(AtlasRoot, Build.BuildId, AttemptId);
        }

        public string AtlasRoot { get; }
        public string AttemptId { get; private set; }
        public GameBuild Build { get; }
        public ResolvedExtractionProfile Profile { get; }
        public ResolvedValidationPolicy Policy { get; }
        public ResolvedExtractionTool Tool { get; }
        public ResolvedExtractionInput Input { get; }
        public InputManifest Manifest { get; }
        public InputSnapshot Snapshot { get; }
        public RecordingRepository Repository { get; }
        public RecordingLockLease Lock { get; private set; }
        public OwnedAttemptPaths Paths { get; private set; }
        public string UnownedStagingSibling { get; }
        public List<string> Events { get; } = [];

        public ExtractionOptions Options => new(
            Build.BuildId,
            Input.GameRoot,
            Tool.ExecutablePath,
            Profile.Profile.ProfileId,
            Retry: false,
            SnapshotInputs: false,
            KeepFailedArtifacts: false);

        public IReadOnlyList<string> ExpectedArguments =>
        [
            $"--game-path={Path.GetFullPath(Input.GameRoot)}",
            "--exe-name=Schedule I",
            $"--output-to={Path.GetFullPath(Paths.OutputRoot)}",
            "--output-as=dll_il_recovery"
        ];

        public ExtractionOrchestrator CreateOrchestrator()
        {
            var dependencies = new ExtractionOrchestratorDependencies
            {
                InitializeRepositoryAsync = _ =>
                {
                    Events.Add("initialize");
                    return Task.CompletedTask;
                },
                RecoverAsync = _ =>
                {
                    Events.Add("recover");
                    return Task.CompletedTask;
                },
                SelectBuildAsync = (_, _) =>
                {
                    Events.Add("select-build");
                    if (_failurePoint == FailurePoint.BuildSelection)
                    {
                        throw new ExtractionOperationException(
                            ExtractionFailureStage.InputResolution,
                            ExtractionFailureCode.BuildNotFound,
                            "build not found");
                    }

                    return Task.FromResult(Build);
                },
                GetProfile = _ =>
                {
                    Events.Add("load-profile");
                    return Profile;
                },
                GetPolicy = _ =>
                {
                    Events.Add("load-policy");
                    return Policy;
                },
                AcquireLockAsync = (_, _) =>
                {
                    Events.Add("acquire-lock");
                    return Task.FromResult<IExtractionOrchestrationLockLease>(Lock);
                },
                AttemptRepository = Repository,
                ResolveToolAsync = (_, _) => ResolveTool(),
                ResolveInputAsync = (_, _, _, _) => ResolveInput(),
                CaptureInputManifestAsync = (_, _, _, _, _) => CaptureManifest(),
                VerifyInputUnchanged = (_, _, _) =>
                {
                    if (_failurePoint == FailurePoint.PostRunMutation)
                    {
                        throw new ExtractionOperationException(
                            ExtractionFailureStage.PostRunInputVerification,
                            ExtractionFailureCode.InputChangedDuringExtraction,
                            "input changed");
                    }
                },
                CreateInputSnapshotAsync = (_, _, _, _) => CreateSnapshotResult(),
                WriteAttemptDocumentAsync = WriteAttemptDocumentAsync,
                ProcessExtractor = new DelegateExtractor(ExtractAsync)
            };

            return new ExtractionOrchestrator(
                AtlasRoot,
                dependencies,
                TimeProvider.System,
                () => AttemptId,
                MoveDirectory);
        }

        public void BeginSecondAttempt(FailurePoint failurePoint)
        {
            _failurePoint = failurePoint;
            _captureCount = 0;
            _documentFailureInjected = false;
            _attemptNumber++;
            AttemptId = Guid.NewGuid().ToString("N");
            Paths = OwnedAttemptPaths.Create(AtlasRoot, Build.BuildId, AttemptId);
            Lock = new RecordingLockLease(Events);
            Events.Clear();
        }

        public void Dispose()
        {
            _input.Dispose();
            if (Directory.Exists(AtlasRoot))
            {
                Directory.Delete(AtlasRoot, recursive: true);
            }
        }

        private Task<ResolvedExtractionTool> ResolveTool()
        {
            Events.Add("resolve-tool");
            return _failurePoint switch
            {
                FailurePoint.ManagedToolAbsent => Task.FromException<ResolvedExtractionTool>(
                    new ToolOperationException("ToolNotInstalled", "tool absent")),
                FailurePoint.CustomProbeFails => Task.FromException<ResolvedExtractionTool>(
                    new ToolOperationException("ToolProbeFailed", "probe failed")),
                _ => Task.FromResult(Tool)
            };
        }

        private Task<ResolvedExtractionInput> ResolveInput()
        {
            Events.Add("resolve-input");
            return _failurePoint == FailurePoint.LiveInputAbsent
                ? Task.FromException<ResolvedExtractionInput>(new ExtractionOperationException(
                    ExtractionFailureStage.InputResolution,
                    ExtractionFailureCode.LiveInputNotFound,
                    "input absent"))
                : Task.FromResult(Input);
        }

        private Task<InputManifest> CaptureManifest()
        {
            _captureCount++;
            Events.Add(_captureCount == 1 ? "capture-pre" : "capture-post");
            if (_failurePoint == FailurePoint.PreRunMismatch && _captureCount == 1)
            {
                return Task.FromException<InputManifest>(new ExtractionOperationException(
                    ExtractionFailureStage.PreRunInputVerification,
                    ExtractionFailureCode.BuildInputMismatch,
                    "input mismatch"));
            }

            return Task.FromResult(Manifest);
        }

        private Task<InputSnapshot> CreateSnapshotResult()
        {
            return _failurePoint switch
            {
                FailurePoint.SnapshotInvalid => Task.FromException<InputSnapshot>(
                    new ExtractionOperationException(
                        ExtractionFailureStage.InputSnapshotCreation,
                        ExtractionFailureCode.ArchivedInputInvalid,
                        "snapshot invalid")),
                FailurePoint.SnapshotFilesystemPromotion => Task.FromException<InputSnapshot>(
                    new ExtractionOperationException(
                        ExtractionFailureStage.InputSnapshotCreation,
                        ExtractionFailureCode.FilesystemPromotionFailed,
                        "snapshot move failed")),
                FailurePoint.SnapshotDatabasePersistence =>
                    MaterializeSnapshotThenFailDatabase(),
                _ => Task.FromResult(MaterializeSnapshot())
            };
        }

        private async Task WriteAttemptDocumentAsync(
            OwnedAttemptPaths paths,
            ExtractionAttempt attempt,
            AttemptExecutionFacts? facts,
            CancellationToken cancellationToken)
        {
            if ((_failurePoint == FailurePoint.ProcessStartDocumentMirror &&
                 attempt.Status == ExtractionAttemptStatus.Running ||
                 _failurePoint == FailurePoint.PreparingDocumentMirror &&
                 attempt.Status == ExtractionAttemptStatus.Preparing) &&
                !_documentFailureInjected)
            {
                _documentFailureInjected = true;
                throw new IOException("injected document mirror failure");
            }

            await _documentStore.WriteAsync(paths, attempt, facts, cancellationToken);
        }

        private async Task<ExtractionProcessResult> ExtractAsync(
            ExtractionProcessRequest request,
            Func<int, CancellationToken, Task> processStarted,
            CancellationToken cancellationToken)
        {
            if (_failurePoint == FailurePoint.ProcessStart)
            {
                CreateStagingLogs(request, "", "");
                return Result(
                    request,
                    ExtractionProcessTerminationReason.StartFailed,
                    processId: null,
                    exitCode: null,
                    startedAtUtc: null,
                    "could not start");
            }

            Events.Add("process-start");
            await processStarted(4242 + _attemptNumber, cancellationToken);
            CreateStagingLogs(request, "stdout", "stderr");
            Directory.CreateDirectory(request.OutputDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(
                    request.OutputDirectory,
                    _failurePoint == FailurePoint.None ? "candidate.bin" : "partial.bin"),
                _failurePoint == FailurePoint.None ? [1, 2, 3] : [1, 2, 3, 4, 5],
                cancellationToken);

            return _failurePoint switch
            {
                FailurePoint.ProcessTimeout or FailurePoint.RetentionMove => Result(
                    request,
                    ExtractionProcessTerminationReason.TimedOut,
                    4242,
                    null,
                    DateTimeOffset.UtcNow,
                    null),
                FailurePoint.ProcessExitNonzero => Result(
                    request,
                    ExtractionProcessTerminationReason.Exited,
                    4242,
                    17,
                    DateTimeOffset.UtcNow,
                    null),
                FailurePoint.CallerCancellation => throw new Cpp2IlProcessCanceledException(
                    processId: 4242,
                    startedAtUtc: DateTimeOffset.UtcNow,
                    completedAtUtc: DateTimeOffset.UtcNow,
                    new ExtractionLogResult(request.StandardOutputPath, 6, 7, true),
                    new ExtractionLogResult(request.StandardErrorPath, 6, 11, true),
                    cancellationToken),
                _ => Result(
                    request,
                    ExtractionProcessTerminationReason.Exited,
                    4242,
                    0,
                    DateTimeOffset.UtcNow,
                    null)
            };
        }

        private void MoveDirectory(string source, string destination)
        {
            if (_failurePoint == FailurePoint.CandidateMove &&
                string.Equals(destination, Paths.CandidateOutputRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected candidate move failure");
            }

            if (_failurePoint == FailurePoint.RetentionMove &&
                string.Equals(destination, Paths.RetainedOutputRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected retention move failure");
            }

            Directory.Move(source, destination);
        }

        private InputSnapshot CreateSnapshot()
        {
            var root = Path.Combine(
                AtlasRoot,
                "builds",
                Build.BuildId,
                "inputs",
                new string('1', 64));
            return new InputSnapshot(
                new string('1', 64),
                Build.BuildId,
                root,
                InputManifestFingerprint.Create(Manifest),
                DateTimeOffset.UtcNow,
                false,
                null,
                Manifest);
        }

        private InputSnapshot MaterializeSnapshot()
        {
            Directory.CreateDirectory(Snapshot.RootPath);
            File.WriteAllText(Path.Combine(Snapshot.RootPath, "snapshot.bin"), "snapshot");
            return Snapshot;
        }

        private Task<InputSnapshot> MaterializeSnapshotThenFailDatabase()
        {
            _ = MaterializeSnapshot();
            return Task.FromException<InputSnapshot>(new ExtractionOperationException(
                ExtractionFailureStage.DatabasePromotion,
                ExtractionFailureCode.DatabasePromotionFailed,
                "snapshot database failed"));
        }

        private static void CreateStagingLogs(
            ExtractionProcessRequest request,
            string stdout,
            string stderr)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.StandardOutputPath)!);
            File.WriteAllText(request.StandardOutputPath, stdout);
            File.WriteAllText(request.StandardErrorPath, stderr);
        }

        private static ExtractionProcessResult Result(
            ExtractionProcessRequest request,
            ExtractionProcessTerminationReason reason,
            int? processId,
            int? exitCode,
            DateTimeOffset? startedAtUtc,
            string? startFailure) => new(
                reason,
                processId,
                exitCode,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                new ExtractionLogResult(
                    request.StandardOutputPath,
                    File.Exists(request.StandardOutputPath)
                        ? new FileInfo(request.StandardOutputPath).Length
                        : 0,
                    0,
                    false),
                new ExtractionLogResult(
                    request.StandardErrorPath,
                    File.Exists(request.StandardErrorPath)
                        ? new FileInfo(request.StandardErrorPath).Length
                        : 0,
                    0,
                    false),
                startFailure);
    }

    private sealed class RecordingRepository(
        List<string> events,
        Func<FailurePoint> failurePoint) : IExtractionRepository
    {
        private bool _runningFailureInjected;
        private readonly List<ExtractionAttempt> _attempts = [];

        public IReadOnlyList<ExtractionAttempt> Attempts => _attempts;
        public List<ExtractionAttemptStatus> RecordedStatuses { get; } = [];

        public Task CreateAttemptAsync(
            ExtractionAttempt attempt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _attempts.Add(attempt);
            RecordedStatuses.Add(attempt.Status);
            events.Add("created");
            return Task.CompletedTask;
        }

        public Task TransitionAttemptAsync(
            ExtractionAttempt attempt,
            ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failurePoint() == FailurePoint.ProcessStartDatabasePersistence &&
                attempt.Status == ExtractionAttemptStatus.Running &&
                !_runningFailureInjected)
            {
                _runningFailureInjected = true;
                throw new IOException("injected database failure");
            }

            var index = _attempts.FindIndex(item => item.AttemptId == attempt.AttemptId);
            Assert.True(index >= 0);
            Assert.Equal(expectedStatus, _attempts[index].Status);
            _attempts[index] = ExtractionAttemptLifecycle.Transition(
                _attempts[index],
                attempt);
            RecordedStatuses.Add(attempt.Status);
            events.Add(attempt.Status switch
            {
                ExtractionAttemptStatus.Preparing => "preparing",
                ExtractionAttemptStatus.Running => "running",
                ExtractionAttemptStatus.ProcessCompleted => "process-completed",
                _ => attempt.Status.ToString().ToLowerInvariant()
            });
            return Task.CompletedTask;
        }

        public Task<ExtractionAttempt?> GetAttemptAsync(
            string attemptId,
            CancellationToken cancellationToken) => Task.FromResult(
                _attempts.SingleOrDefault(item => item.AttemptId == attemptId));

        public Task<GameBuild?> GetBuildAsync(
            string buildId,
            CancellationToken cancellationToken) => Task.FromResult<GameBuild?>(null);

        public Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(
            string buildId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstallationObservationRecord>>([]);

        public Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtractionAttempt>>([]);

        public Task SaveInputSnapshotAsync(
            InputSnapshot snapshot,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
            string buildId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InputSnapshot>>([]);

        public Task<InputSnapshot?> GetInputSnapshotAsync(
            string inputSnapshotId,
            CancellationToken cancellationToken) => Task.FromResult<InputSnapshot?>(null);

        public Task MarkInputSnapshotReplayVerifiedAsync(
            string inputSnapshotId,
            string expectedBuildId,
            string expectedManifestDigest,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLockLease(List<string> events)
        : IExtractionOrchestrationLockLease
    {
        public bool Released { get; private set; }

        public Task UpdateChildProcessIdAsync(
            int childProcessId,
            CancellationToken cancellationToken)
        {
            Assert.True(childProcessId > 0);
            events.Add("lock-child");
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (!Released)
            {
                events.Add("release-lock");
                Released = true;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegateExtractor(
        Func<
            ExtractionProcessRequest,
            Func<int, CancellationToken, Task>,
            CancellationToken,
            Task<ExtractionProcessResult>> extract) : IIl2CppExtractor
    {
        public Task<ExtractionProcessResult> ExtractAsync(
            ExtractionProcessRequest request,
            Func<int, CancellationToken, Task> processStarted,
            CancellationToken cancellationToken) => extract(
                request,
                processStarted,
                cancellationToken);
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
