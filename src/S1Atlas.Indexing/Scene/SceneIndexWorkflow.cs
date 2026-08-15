using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Paths;

namespace S1Atlas.Indexing.Scene;

public sealed class SceneIndexWorkflow
{
    public const string ParserId = "assetstools-net";
    public const string ParserVersion = "3.0.5";
    public const int SerializedFileSchemaVersion = 22;

    private static readonly string[] SupportedContainerPaths =
    [
        "Schedule I_Data/level0",
        "Schedule I_Data/level1",
        "Schedule I_Data/level2",
        "Schedule I_Data/sharedassets0.assets",
        "Schedule I_Data/sharedassets1.assets",
        "Schedule I_Data/sharedassets2.assets",
        "Schedule I_Data/resources.assets",
        "Schedule I_Data/globalgamemanagers",
        "Schedule I_Data/globalgamemanagers.assets"
    ];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Regex SupportedUnityVersionPattern = new(
        "^2022\\.3\\.62(?:[a-z]+\\d+)?$",
        RegexOptions.CultureInvariant);

    private readonly string _dataRoot;
    private readonly IIndexRepository _repository;
    private readonly ISceneRepository _sceneRepository;
    private readonly IExtractionRepository _extractionRepository;
    private readonly IAtlasRepository _atlasRepository;
    private readonly Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> _authorityResolver;
    private readonly SceneInputVerifier _inputVerifier;
    private readonly IUnitySerializedFileParser _parser;
    private readonly SceneNormalizer _normalizer;
    private readonly string _parserVersion;

    public SceneIndexWorkflow(
        string dataRoot,
        IIndexRepository repository,
        IExtractionRepository extractionRepository,
        IAtlasRepository atlasRepository,
        PreferredVerifiedExtractionResolver authorityResolver,
        SceneInputVerifier inputVerifier,
        IUnitySerializedFileParser parser,
        SceneNormalizer normalizer,
        string parserVersion = ParserVersion)
        : this(
            dataRoot,
            repository,
            extractionRepository,
            atlasRepository,
            authorityResolver is null
                ? throw new ArgumentNullException(nameof(authorityResolver))
                : authorityResolver.ResolveAsync,
            inputVerifier,
            parser,
            normalizer,
            parserVersion)
    {
    }

    internal SceneIndexWorkflow(
        string dataRoot,
        IIndexRepository repository,
        IExtractionRepository extractionRepository,
        IAtlasRepository atlasRepository,
        Func<string, CancellationToken, Task<PreferredVerifiedExtraction?>> authorityResolver,
        SceneInputVerifier inputVerifier,
        IUnitySerializedFileParser parser,
        SceneNormalizer normalizer,
        string parserVersion = ParserVersion)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sceneRepository = _repository.RequireSceneRepository();
        _extractionRepository = extractionRepository ?? throw new ArgumentNullException(nameof(extractionRepository));
        _atlasRepository = atlasRepository ?? throw new ArgumentNullException(nameof(atlasRepository));
        _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
        _inputVerifier = inputVerifier ?? throw new ArgumentNullException(nameof(inputVerifier));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        _parserVersion = parserVersion;
    }

    public async Task<SceneIndexWorkflowResult> RunScheduleOneAsync(
        string buildId,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        cancellationToken.ThrowIfCancellationRequested();

        var authority = await RequireAuthorityAsync(buildId, cancellationToken);
        var inputSnapshot = await RequireReplayVerifiedInputAsync(authority, buildId, cancellationToken);
        var environment = await RequireEnvironmentAsync(buildId, cancellationToken);
        var code = await RequireCodeIndexAsync(authority, cancellationToken);
        var declarations = SelectSceneContainers(environment.Installation.InstallationRoot!);
        VerifiedSceneInput verifiedInput;
        try
        {
            verifiedInput = await _inputVerifier.CaptureAsync(
                environment.Installation.InstallationRoot!,
                declarations,
                cancellationToken);
        }
        catch (IOException exception)
        {
            throw new SceneIndexFailureException(SceneQueryStatus.SceneInputIntegrityFailure, exception.Message, exception);
        }
        RequireSupportedUnityVersion(verifiedInput.Containers);
        var parserVersion = force ? _parserVersion + ":forced:" + Guid.NewGuid().ToString("N") : _parserVersion;
        var sceneSnapshotId = SceneSnapshotIdentity.Create(
            buildId,
            authority.Extraction.ExtractionId,
            inputSnapshot.ManifestDigest,
            code.Run.IndexId,
            ParserId,
            parserVersion,
            SerializedFileSchemaVersion,
            verifiedInput.Containers.Select(container => new SceneSnapshotContainerFact(
                container.RelativePath,
                container.ByteCount,
                container.Sha256,
                container.SidecarManifest)).ToArray());

        if (!force)
        {
            var completed = await _sceneRepository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, cancellationToken);
            if (completed is not null)
                return await ToResultAsync(completed, cancellationToken);
        }

        var paths = OwnedScenePaths.ForScheduleOne(_dataRoot, buildId, sceneSnapshotId);
        if (Directory.Exists(paths.FinalRoot))
            DeleteOwnedFinal(buildId, sceneSnapshotId);

        DeleteOwnedStaging(buildId, sceneSnapshotId);
        Directory.CreateDirectory(paths.StagingRoot);
        var snapshot = new SceneSnapshotRecord(
            sceneSnapshotId,
            buildId,
            authority.Extraction.ExtractionId,
            inputSnapshot.InputSnapshotId,
            code.Snapshot.SnapshotId,
            code.Run.IndexId,
            ParserId,
            parserVersion,
            verifiedInput.ManifestDigest,
            SceneSnapshotStatus.Running,
            SceneRecoveryStatus.Unknown,
            DateTimeOffset.UtcNow.ToString("O"));
        var snapshotCreated = false;
        var databasePublished = false;
        var finalPromoted = false;
        try
        {
            await _sceneRepository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
            snapshotCreated = true;
            await _sceneRepository.StartSceneSnapshotAsync(sceneSnapshotId, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);

            var parsed = await _parser.ParseAsync(verifiedInput.Containers, cancellationToken);
            RequireParserFacts(parsed, verifiedInput.Containers);
            var writeSet = await _normalizer.NormalizeAsync(snapshot, verifiedInput.Containers, parsed, cancellationToken);
            RequireBoundedWriteSet(writeSet, verifiedInput.Containers.Count);
            await WriteStagingManifestAsync(paths.StagingRoot, writeSet, verifiedInput.Containers, cancellationToken);
            await _inputVerifier.VerifyAfterParsingAsync(verifiedInput, cancellationToken);
            await RequireStableAuthoritiesAsync(authority, inputSnapshot, code, buildId, cancellationToken);

            await _sceneRepository.CompleteSceneSnapshotAsync(
                sceneSnapshotId,
                writeSet,
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
            finalPromoted = true;
            await File.WriteAllTextAsync(paths.CompleteMarkerPath, sceneSnapshotId + "\n", cancellationToken);
            await _sceneRepository.PublishSceneSnapshotAsync(
                sceneSnapshotId,
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
            databasePublished = true;
            return ToResult(writeSet.Snapshot, reused: false, writeSet);
        }
        catch (Exception exception)
        {
            if (snapshotCreated && !databasePublished)
            {
                try
                {
                    await _sceneRepository.FailSceneSnapshotAsync(
                        sceneSnapshotId,
                        FailureCode(exception),
                        exception.Message,
                        DateTimeOffset.UtcNow.ToString("O"),
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            if (finalPromoted && !databasePublished)
                DeleteOwnedFinal(buildId, sceneSnapshotId);
            DeleteOwnedStaging(buildId, sceneSnapshotId);
            throw;
        }
    }

    private async Task<PreferredVerifiedExtraction> RequireAuthorityAsync(string buildId, CancellationToken cancellationToken)
    {
        var authority = await _authorityResolver(buildId, cancellationToken);
        if (authority is null ||
            !string.Equals(authority.BuildId, buildId, StringComparison.Ordinal) ||
            !string.Equals(authority.Extraction.BuildId, buildId, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoPreferredVerifiedExtraction,
                "No preferred replay-verified extraction exists for the requested build.");
        }

        return authority;
    }

    private async Task<InputSnapshot> RequireReplayVerifiedInputAsync(
        PreferredVerifiedExtraction authority,
        string buildId,
        CancellationToken cancellationToken)
    {
        var attempt = await _extractionRepository.GetAttemptAsync(authority.Extraction.SourceAttemptId, cancellationToken);
        if (attempt?.InputSnapshotId is not { Length: > 0 } inputSnapshotId ||
            !string.Equals(attempt.BuildId, buildId, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoReplayVerifiedExtractionInput,
                "The preferred extraction does not identify a replay-verified input snapshot for the requested build.");
        }

        var input = await _extractionRepository.GetInputSnapshotAsync(inputSnapshotId, cancellationToken);
        if (input is null || !input.ReplayVerified ||
            !string.Equals(input.BuildId, buildId, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoReplayVerifiedExtractionInput,
                "The preferred extraction input snapshot is not replay-verified for the requested build.");
        }

        return input;
    }

    private async Task<EnvironmentSnapshot> RequireEnvironmentAsync(string buildId, CancellationToken cancellationToken)
    {
        var environment = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        if (environment is null ||
            !string.Equals(environment.Build.BuildId, buildId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(environment.Installation.InstallationRoot))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoMatchingEnvironmentSnapshot,
                "The current environment snapshot does not match the requested scene-index build.");
        }

        return environment;
    }

    private async Task<CodeIndexAuthority> RequireCodeIndexAsync(
        PreferredVerifiedExtraction authority,
        CancellationToken cancellationToken)
    {
        var run = await _repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            environmentSnapshotId: null,
            cancellationToken);
        if (run is null)
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoCompletedScheduleOneCodeIndex,
                "No completed Schedule I Installed code index is available for scene linking.");

        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        if (snapshot is null ||
            snapshot.Codebase != CodebaseKind.ScheduleI ||
            snapshot.Channel != CodeChannel.Installed ||
            string.IsNullOrWhiteSpace(snapshot.EnvironmentSnapshotId) ||
            !string.Equals(snapshot.SourceIdentity, authority.Extraction.ExtractionId, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.CrossBuildCodeIndex,
                "The completed Schedule I Installed code index does not belong to the selected extraction and build.");
        }

        return new CodeIndexAuthority(run, snapshot);
    }

    private async Task RequireStableAuthoritiesAsync(
        PreferredVerifiedExtraction authority,
        InputSnapshot inputSnapshot,
        CodeIndexAuthority code,
        string buildId,
        CancellationToken cancellationToken)
    {
        var finalAuthority = await RequireAuthorityAsync(buildId, cancellationToken);
        if (!string.Equals(finalAuthority.Extraction.ExtractionId, authority.Extraction.ExtractionId, StringComparison.Ordinal) ||
            !Equals(finalAuthority.Preference, authority.Preference))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.PreferredExtractionChanged,
                "The preferred verified extraction changed while scene indexing was running.");
        }

        var finalInput = await RequireReplayVerifiedInputAsync(finalAuthority, buildId, cancellationToken);
        if (!string.Equals(finalInput.InputSnapshotId, inputSnapshot.InputSnapshotId, StringComparison.Ordinal) ||
            !string.Equals(finalInput.ManifestDigest, inputSnapshot.ManifestDigest, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.ReplayVerifiedInputChanged,
                "The replay-verified extraction input changed while scene indexing was running.");
        }

        var finalCode = await RequireCodeIndexAsync(finalAuthority, cancellationToken);
        if (!string.Equals(finalCode.Run.IndexId, code.Run.IndexId, StringComparison.Ordinal) ||
            !string.Equals(finalCode.Snapshot.SnapshotId, code.Snapshot.SnapshotId, StringComparison.Ordinal))
        {
            throw new SceneIndexFailureException(
                SceneQueryStatus.CodeIndexChanged,
                "The completed Schedule I Installed code index changed while scene indexing was running.");
        }
    }

    private static IReadOnlyList<SceneContainerDeclaration> SelectSceneContainers(string installRoot)
    {
        var root = Path.GetFullPath(installRoot);
        var declarations = SupportedContainerPaths
            .Where(relativePath => File.Exists(Path.Combine(root, relativePath)))
            .Select(relativePath => new SceneContainerDeclaration(
                relativePath,
                Sidecars(root, relativePath)))
            .ToArray();
        if (declarations.Length == 0)
            throw new SceneIndexFailureException(
                SceneQueryStatus.NoVerifiedSceneContainers,
                "No allowlisted verified scene containers exist under the selected installation root.");
        return declarations;
    }

    private static IReadOnlyList<string> Sidecars(string root, string primaryRelativePath)
    {
        var candidates = new List<string>
        {
            primaryRelativePath + ".resS",
            primaryRelativePath + ".resource"
        };
        if (primaryRelativePath.EndsWith(".assets", StringComparison.Ordinal))
            candidates.Add(primaryRelativePath[..^".assets".Length] + ".resource");
        return candidates.Where(relativePath => File.Exists(Path.Combine(root, relativePath))).ToArray();
    }

    private static void RequireSupportedUnityVersion(IReadOnlyList<VerifiedSceneContainer> containers)
    {
        if (containers.Any(container => !SupportedUnityVersionPattern.IsMatch(container.UnityVersion)))
            throw new SceneIndexFailureException(SceneQueryStatus.UnsupportedContainer, "Scene inputs must target Unity 2022.3.62.");
    }

    private static void RequireParserFacts(
        IReadOnlyList<ParsedSceneContainer> parsed,
        IReadOnlyList<VerifiedSceneContainer> verified)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Count != verified.Count || parsed.Any(container => container.SerializedFileVersion != SerializedFileSchemaVersion))
            throw new InvalidDataException("Scene parser did not produce the required class-ID facts for the verified container set.");
    }

    private static void RequireBoundedWriteSet(SceneWriteSet writeSet, int verifiedContainerCount)
    {
        if (writeSet.Containers.Count != verifiedContainerCount || writeSet.Containers.Count > SupportedContainerPaths.Length)
            throw new InvalidDataException("Scene normalization produced an invalid container write set.");
    }

    private void DeleteOwnedStaging(string buildId, string sceneSnapshotId)
    {
        var paths = OwnedScenePaths.ForScheduleOne(_dataRoot, buildId, sceneSnapshotId);
        if (Directory.Exists(paths.StagingRoot))
            Directory.Delete(paths.StagingRoot, recursive: true);
    }

    private void DeleteOwnedFinal(string buildId, string sceneSnapshotId)
    {
        var paths = OwnedScenePaths.ForScheduleOne(_dataRoot, buildId, sceneSnapshotId);
        if (Directory.Exists(paths.FinalRoot))
            Directory.Delete(paths.FinalRoot, recursive: true);
    }

    private static async Task WriteStagingManifestAsync(
        string stagingRoot,
        SceneWriteSet writeSet,
        IReadOnlyList<VerifiedSceneContainer> verifiedContainers,
        CancellationToken cancellationToken)
    {
        var payload = new SceneIndexStagingPayload(
            writeSet.Snapshot.SceneSnapshotId,
            writeSet.Snapshot.ContainerManifestDigest,
            new SceneIndexCounts(
                writeSet.Containers.Count,
                writeSet.Documents.Count,
                writeSet.GameObjects.Count,
                writeSet.Transforms.Count,
                writeSet.Components.Count,
                writeSet.References.Count),
            verifiedContainers
                .OrderBy(container => container.RelativePath, StringComparer.Ordinal)
                .Select(container => new SceneIndexContainerManifest(
                    container.RelativePath,
                    container.ByteCount,
                    container.Sha256,
                    container.SidecarManifest))
                .ToArray());
        var payloadJson = JsonSerializer.Serialize(payload, ManifestJsonOptions);
        var manifest = new SceneIndexStagingManifest(
            payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant());
        var path = Path.Combine(stagingRoot, "scene-index.manifest.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static SceneIndexWorkflowResult ToResult(SceneSnapshotRecord snapshot, bool reused, SceneWriteSet? writeSet) =>
        new(
            snapshot.SceneSnapshotId,
            snapshot.BuildId,
            snapshot.CodeIndexId,
            snapshot.ParserId,
            snapshot.ParserVersion,
            reused,
            writeSet?.Containers.Count ?? 0,
            writeSet?.Documents.Count ?? 0,
            writeSet?.GameObjects.Count ?? 0,
            writeSet?.Transforms.Count ?? 0,
            writeSet?.Components.Count ?? 0,
            writeSet?.References.Count ?? 0,
            writeSet is null ? new Dictionary<string, int>() : RecoveryCounts(writeSet),
            []);

    private async Task<SceneIndexWorkflowResult> ToResultAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var statistics = await _sceneRepository.GetSceneIndexStatisticsAsync(snapshot.SceneSnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("PublishedSceneSnapshotStatisticsMissing");
        return new SceneIndexWorkflowResult(
            snapshot.SceneSnapshotId,
            snapshot.BuildId,
            snapshot.CodeIndexId,
            snapshot.ParserId,
            snapshot.ParserVersion,
            true,
            statistics.ContainerCount,
            statistics.DocumentCount,
            statistics.GameObjectCount,
            statistics.TransformCount,
            statistics.ComponentCount,
            statistics.ReferenceCount,
            statistics.RecoveryCounts,
            []);
    }

    private static IReadOnlyDictionary<string, int> RecoveryCounts(SceneWriteSet writeSet) =>
        writeSet.Documents.Select(row => row.RecoveryStatus)
            .Concat(writeSet.GameObjects.Select(row => row.RecoveryStatus))
            .Concat(writeSet.Transforms.Select(row => row.RecoveryStatus))
            .Concat(writeSet.Components.Select(row => row.RecoveryStatus))
            .Concat(writeSet.References.Select(row => row.RecoveryStatus))
            .GroupBy(status => status.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static string FailureCode(Exception exception) =>
        exception is OperationCanceledException ? "Canceled" : "SceneIndexingFailed";

    private sealed record CodeIndexAuthority(IndexRunRecord Run, CodeSnapshotRecord Snapshot);
    private sealed record SceneIndexStagingManifest(SceneIndexStagingPayload Payload, string SceneDataSha256);
    private sealed record SceneIndexStagingPayload(string SceneSnapshotId, string ContainerManifestDigest, SceneIndexCounts Counts, IReadOnlyList<SceneIndexContainerManifest> Containers);
    private sealed record SceneIndexCounts(int ContainerCount, int SceneCount, int GameObjectCount, int TransformCount, int ComponentCount, int ReferenceCount);
    private sealed record SceneIndexContainerManifest(string RelativePath, long ByteCount, string Sha256, string SidecarManifest);
}
