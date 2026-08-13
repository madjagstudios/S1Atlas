using System.Text.Json;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Processes;
using S1Atlas.Extraction.Tests.Inputs;
using S1Atlas.Extraction.Tests.Processes;
using Xunit;

namespace S1Atlas.Extraction.Tests.Cpp2Il;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Cpp2IlProcessEnvironmentCollection
{
    public const string Name = "Cpp2IL process environment";
}

[Collection(Cpp2IlProcessEnvironmentCollection.Name)]
public sealed class Cpp2IlProcessExtractorTests : IDisposable
{
    private readonly string _temporaryDirectory = CreateTemporaryDirectory();

    [Fact]
    public async Task ExtractAsync_SourceBuiltFakeReceivesTypedArgumentsAndWritesOnlyRequestedOutput()
    {
        var request = CreateRequest();
        var argumentRecord = Path.Combine(_temporaryDirectory, "child-arguments.json");
        IReadOnlyList<string>? deliveredArguments = null;
        var callbackObserved = false;
        var extractor = new Cpp2IlProcessExtractor(async (processRequest, callback, token) =>
        {
            deliveredArguments = processRequest.Arguments;
            return await new ProcessRunner().RunAsync(
                processRequest,
                async (processId, callbackToken) =>
                {
                    callbackObserved = true;
                    await callback(processId, callbackToken);
                },
                token);
        });

        const string recordVariable = "S1ATLAS_FAKE_ARGUMENT_RECORD";
        var inheritedRecord = Environment.GetEnvironmentVariable(recordVariable);
        ExtractionProcessResult result;
        try
        {
            Environment.SetEnvironmentVariable(recordVariable, argumentRecord);
            result = await extractor.ExtractAsync(
                request,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(recordVariable, inheritedRecord);
        }

        Assert.True(callbackObserved);
        Assert.Equal(ExtractionProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                $"--game-path={Path.GetFullPath(request.GameRoot)}",
                "--exe-name=Schedule I",
                $"--output-to={Path.GetFullPath(request.OutputDirectory)}",
                "--output-as=dll_il_recovery"
            ],
            deliveredArguments!);
        Assert.Equal(
            deliveredArguments,
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
                argumentRecord,
                TestContext.Current.CancellationToken)));
        Assert.True(File.Exists(Path.Combine(
            request.OutputDirectory,
            "S1Atlas.FakeAssembly.dll")));
        Assert.Empty(Directory.EnumerateFiles(request.WorkingDirectory));
        Assert.Equal(
            ["S1Atlas.FakeAssembly.dll"],
            Directory.EnumerateFiles(request.OutputDirectory)
                .Select(path => Path.GetFileName(path)!)
                .ToArray());
    }

    [Fact]
    public async Task ExtractAsync_UsesOnlyAtlasOwnedPathsAndTypedProfileExecutionLimits()
    {
        ProcessRequest? captured = null;
        var extractor = new Cpp2IlProcessExtractor((processRequest, _, _) =>
        {
            captured = processRequest;
            return Task.FromResult(CreateProcessResult(
                processRequest,
                ProcessTerminationReason.Exited,
                exitCode: 0));
        });
        var request = CreateRequest(
            InputTestFixture.Profile with
            {
                Timeout = TimeSpan.FromSeconds(37),
                MaximumRetainedStandardOutputBytes = 1234,
                MaximumRetainedStandardErrorBytes = 5678
            });

        await extractor.ExtractAsync(
            request,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(request.WorkingDirectory, captured.WorkingDirectory);
        Assert.Equal(request.StandardOutputPath, captured.StandardOutputPath);
        Assert.Equal(request.StandardErrorPath, captured.StandardErrorPath);
        Assert.Equal(TimeSpan.FromSeconds(37), captured.Timeout);
        Assert.Equal(1234, captured.MaximumRetainedStandardOutputBytes);
        Assert.Equal(5678, captured.MaximumRetainedStandardErrorBytes);
        Assert.Equal(
            new Dictionary<string, string?> { ["NO_COLOR"] = "true" },
            captured.EnvironmentOverrides);
    }

    [Fact]
    public async Task ExtractAsync_NonzeroExitIsReturnedForOrchestrationClassification()
    {
        var extractor = CreateResultExtractor(
            ProcessTerminationReason.Exited,
            exitCode: 23);

        var result = await extractor.ExtractAsync(
            CreateRequest(),
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtractionProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(23, result.ExitCode);
    }

    [Fact]
    public async Task ExtractAsync_TimeoutRemainsDistinctFromCallerCancellation()
    {
        var timeout = await CreateResultExtractor(
                ProcessTerminationReason.TimedOut,
                exitCode: null)
            .ExtractAsync(
                CreateRequest(),
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateResultExtractor(ProcessTerminationReason.Canceled, exitCode: null)
                .ExtractAsync(
                    CreateRequest(),
                    (_, _) => Task.CompletedTask,
                    cancellation.Token));

        Assert.Equal(ExtractionProcessTerminationReason.TimedOut, timeout.TerminationReason);
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellationPreservesDrainedLogAndProcessFacts()
    {
        using var cancellation = new CancellationTokenSource();
        var request = CreateRequest();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var completedAt = DateTimeOffset.UtcNow;
        var extractor = new Cpp2IlProcessExtractor((processRequest, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ProcessResult(
                ProcessTerminationReason.Canceled,
                987,
                ExitCode: null,
                startedAt,
                completedAt,
                new BoundedLogResult(
                    processRequest.StandardOutputPath,
                    RetainedBytes: 12,
                    DiscardedBytes: 34,
                    Truncated: true),
                new BoundedLogResult(
                    processRequest.StandardErrorPath,
                    RetainedBytes: 56,
                    DiscardedBytes: 78,
                    Truncated: true),
                StartFailureMessage: null,
                StartPersistenceException: null));
        });

        var exception = await Assert.ThrowsAsync<Cpp2IlProcessCanceledException>(() =>
            extractor.ExtractAsync(
                request,
                (_, _) => Task.CompletedTask,
                cancellation.Token));

        Assert.Equal(987, exception.ProcessId);
        Assert.Equal(startedAt, exception.StartedAtUtc);
        Assert.Equal(completedAt, exception.CompletedAtUtc);
        Assert.Equal(34, exception.StandardOutput.DiscardedBytes);
        Assert.True(exception.StandardOutput.Truncated);
        Assert.Equal(78, exception.StandardError.DiscardedBytes);
        Assert.True(exception.StandardError.Truncated);
    }

    [Fact]
    public async Task ExtractAsync_WhenStartPersistenceFails_RethrowsTheCallbackFailure()
    {
        var callbackFailure = new InvalidOperationException("persistence failed");
        var request = CreateRequest();
        var extractor = new Cpp2IlProcessExtractor((processRequest, _, _) =>
            Task.FromResult(CreateProcessResult(
                processRequest,
                ProcessTerminationReason.StartPersistenceFailed,
                exitCode: null,
                startPersistenceException: callbackFailure)));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extractor.ExtractAsync(
                request,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Same(callbackFailure, thrown);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task ExtractAsync_WhenExecutionPathIsOutsideOwnedStaging_RejectsBeforeRunner(
        string path)
    {
        var invoked = false;
        var extractor = new Cpp2IlProcessExtractor((_, _, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("runner must not be called");
        });
        var request = CreateRequest();
        request = path switch
        {
            "working" => request with
            {
                WorkingDirectory = Path.Combine(_temporaryDirectory, "outside-working")
            },
            "stdout" => request with
            {
                StandardOutputPath = Path.Combine(_temporaryDirectory, "outside.stdout.log")
            },
            "stderr" => request with
            {
                StandardErrorPath = Path.Combine(_temporaryDirectory, "outside.stderr.log")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

        await Assert.ThrowsAsync<ArgumentException>(() => extractor.ExtractAsync(
            request,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private Cpp2IlProcessExtractor CreateResultExtractor(
        ProcessTerminationReason reason,
        int? exitCode) => new((request, _, _) => Task.FromResult(
            CreateProcessResult(request, reason, exitCode)));

    private ExtractionProcessRequest CreateRequest(ExtractionProfile? profile = null)
    {
        var stagingRoot = Path.Combine(
            _temporaryDirectory,
            "atlas",
            "builds",
            "test-build",
            "extractions",
            ".staging",
            Guid.NewGuid().ToString("N"));
        var working = Path.Combine(stagingRoot, "working");
        var output = Path.Combine(stagingRoot, "output");
        var logs = Path.Combine(stagingRoot, "logs");
        var game = Path.Combine(_temporaryDirectory, "game with spaces & (雪) $`literal`");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(game);

        return new ExtractionProcessRequest(
            FakeCpp2IlLocator.ExecutablePath,
            game,
            working,
            output,
            Path.Combine(logs, "stdout.log"),
            Path.Combine(logs, "stderr.log"),
            new ResolvedExtractionProfile(
                profile ?? InputTestFixture.Profile,
                new string('a', 64)));
    }

    private static ProcessResult CreateProcessResult(
        ProcessRequest request,
        ProcessTerminationReason reason,
        int? exitCode,
        Exception? startPersistenceException = null) => new(
            reason,
            ProcessId: reason == ProcessTerminationReason.StartFailed ? null : 123,
            exitCode,
            StartedAtUtc: reason == ProcessTerminationReason.StartFailed
                ? null
                : DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            new BoundedLogResult(request.StandardOutputPath, 0, 0, false),
            new BoundedLogResult(request.StandardErrorPath, 0, 0, false),
            StartFailureMessage: reason is ProcessTerminationReason.StartFailed or
                ProcessTerminationReason.StartPersistenceFailed
                    ? "start failure"
                    : null,
            startPersistenceException);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-cpp2il-process-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
