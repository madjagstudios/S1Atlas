using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;

namespace S1Atlas.Extraction.Inputs;

internal sealed class ExtractionInputResolver
{
    private readonly IAtlasRepository _atlasRepository;
    private readonly IExtractionRepository _extractionRepository;
    private readonly WindowsScheduleOneLocator _windowsLocator;
    private readonly LiveInputVerifier _verifier;
    private readonly IFileHasher _fileHasher;
    private readonly InputSnapshotDocumentStore _documentStore = new();

    public ExtractionInputResolver(
        IAtlasRepository atlasRepository,
        IExtractionRepository extractionRepository,
        WindowsScheduleOneLocator windowsLocator,
        LiveInputVerifier verifier,
        IFileHasher fileHasher)
    {
        _atlasRepository = atlasRepository ??
            throw new ArgumentNullException(nameof(atlasRepository));
        _extractionRepository = extractionRepository ??
            throw new ArgumentNullException(nameof(extractionRepository));
        _windowsLocator = windowsLocator ??
            throw new ArgumentNullException(nameof(windowsLocator));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
    }

    public async Task<GameBuild> SelectBuildAsync(
        string? requestedBuildId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameBuild? build;
        if (string.IsNullOrWhiteSpace(requestedBuildId))
        {
            build = (await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken))?.Build;
        }
        else
        {
            build = await _extractionRepository.GetBuildAsync(
                requestedBuildId,
                cancellationToken);
        }

        if (build is null)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputResolution,
                ExtractionFailureCode.BuildNotFound,
                string.IsNullOrWhiteSpace(requestedBuildId)
                    ? "No current Atlas build exists. Run 's1atlas scan' first."
                    : $"Atlas build '{requestedBuildId}' was not found.");
        }

        return build;
    }

    public async Task<ResolvedExtractionInput> ResolveAsync(
        GameBuild build,
        string? explicitGamePath,
        string? explicitInputSnapshotId,
        ExtractionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(explicitInputSnapshotId))
        {
            if (!string.IsNullOrWhiteSpace(explicitGamePath))
            {
                throw new ExtractionOperationException(
                    ExtractionFailureStage.InputResolution,
                    ExtractionFailureCode.ArchivedInputInvalid,
                    "An explicit input snapshot cannot be combined with a live game path.");
            }

            return await ResolveExplicitArchivedAsync(
                build,
                explicitInputSnapshotId,
                profile,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(explicitGamePath))
        {
            var explicitInput = CreateInput(
                explicitGamePath,
                ExtractionInputSource.Live,
                inputSnapshotId: null,
                profile,
                explicitCandidate: true);
            await _verifier.CaptureAsync(
                explicitInput,
                build,
                profile,
                cancellationToken);
            return explicitInput;
        }

        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observations = await _extractionRepository.ListInstallationObservationsAsync(
            build.BuildId,
            cancellationToken);
        foreach (var observation in observations
                     .OrderByDescending(item => item.CapturedAtUtc)
                     .ThenByDescending(item => item.SnapshotId, StringComparer.Ordinal))
        {
            var resolved = await TryResolveImplicitLiveAsync(
                observation.InstallationRoot,
                build,
                profile,
                seenRoots,
                cancellationToken);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        foreach (var candidate in _windowsLocator.GetCandidatePaths(overridePath: null))
        {
            var resolved = await TryResolveImplicitLiveAsync(
                candidate,
                build,
                profile,
                seenRoots,
                cancellationToken);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var invalidArchiveFound = false;
        var snapshots = await _extractionRepository.ListReplayVerifiedInputSnapshotsAsync(
            build.BuildId,
            cancellationToken);
        foreach (var snapshot in snapshots
                     .OrderByDescending(item => item.CreatedAtUtc)
                     .ThenByDescending(item => item.InputSnapshotId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var gameRoot = InputSnapshotDocumentStore.GetGameRoot(snapshot.RootPath);
                if (!seenRoots.Add(NormalizePath(snapshot.RootPath)))
                {
                    continue;
                }

                // Implicit historical resolution consumes only replay-verified snapshots.
                ValidateStoredSnapshot(snapshot, build);
                var verified = await _documentStore.TryReadVerifiedAsync(
                    snapshot.RootPath,
                    build.BuildId,
                    snapshot.InputSnapshotId,
                    _fileHasher,
                    cancellationToken);
                if (verified is null ||
                    !string.Equals(
                        verified.ManifestDigest,
                        snapshot.ManifestDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Archived input bytes no longer match the verified manifest.");
                }

                return CreateInput(
                    gameRoot,
                    ExtractionInputSource.ArchivedSnapshot,
                    snapshot.InputSnapshotId,
                    profile,
                    explicitCandidate: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ExtractionOperationException or InvalidDataException or
                ArgumentException or IOException or UnauthorizedAccessException)
            {
                invalidArchiveFound = true;
            }
        }

        if (invalidArchiveFound)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputResolution,
                ExtractionFailureCode.ArchivedInputInvalid,
                "No replay-verified archived input remains valid for the selected build.");
        }

        throw new ExtractionOperationException(
            ExtractionFailureStage.InputResolution,
            ExtractionFailureCode.LiveInputNotFound,
            "No matching local or archived game input was found. Run 's1atlas scan' to refresh installation observations.");
    }

    /// <summary>
    /// Resolves exactly one stored snapshot for an explicit archived-only run. It never
    /// falls back to live input and may select an unverified snapshot solely so an
    /// authoritative replay can certify it. The Cpp2IL root is the snapshot's contained
    /// <c>game-root</c>, never the snapshot document root.
    /// </summary>
    private async Task<ResolvedExtractionInput> ResolveExplicitArchivedAsync(
        GameBuild build,
        string explicitInputSnapshotId,
        ExtractionProfile profile,
        CancellationToken cancellationToken)
    {
        var snapshot = await _extractionRepository.GetInputSnapshotAsync(
            explicitInputSnapshotId,
            cancellationToken);
        if (snapshot is null)
        {
            throw ArchivedInvalid(
                $"Input snapshot '{explicitInputSnapshotId}' does not exist.");
        }

        if (!string.Equals(snapshot.BuildId, build.BuildId, StringComparison.Ordinal))
        {
            throw ArchivedInvalid(
                $"Input snapshot '{explicitInputSnapshotId}' belongs to a different build.");
        }

        InputSnapshot? verified;
        try
        {
            verified = await _documentStore.TryReadVerifiedAsync(
                snapshot.RootPath,
                build.BuildId,
                snapshot.InputSnapshotId,
                _fileHasher,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw ArchivedInvalid(
                $"Input snapshot '{explicitInputSnapshotId}' could not be verified.",
                exception);
        }

        if (verified is null ||
            !string.Equals(
                verified.ManifestDigest,
                snapshot.ManifestDigest,
                StringComparison.Ordinal))
        {
            throw ArchivedInvalid(
                $"Input snapshot '{explicitInputSnapshotId}' failed strict byte verification.");
        }

        RequireBuildInputHashes(verified.Manifest, build, explicitInputSnapshotId);

        return CreateInput(
            InputSnapshotDocumentStore.GetGameRoot(snapshot.RootPath),
            ExtractionInputSource.ArchivedSnapshot,
            snapshot.InputSnapshotId,
            profile,
            explicitCandidate: true);
    }

    private static void RequireBuildInputHashes(
        InputManifest manifest,
        GameBuild build,
        string snapshotId)
    {
        var assembly = manifest.Entries.SingleOrDefault(entry => entry.Role == "gameAssembly");
        var metadata = manifest.Entries.SingleOrDefault(entry => entry.Role == "globalMetadata");
        if (assembly?.Sha256 != build.GameAssemblySha256 ||
            metadata?.Sha256 != build.MetadataSha256)
        {
            throw ArchivedInvalid(
                $"Input snapshot '{snapshotId}' does not match the selected build.");
        }
    }

    private static ExtractionOperationException ArchivedInvalid(
        string message,
        Exception? innerException = null) =>
        new(
            ExtractionFailureStage.InputResolution,
            ExtractionFailureCode.ArchivedInputInvalid,
            message,
            innerException: innerException);

    private async Task<ResolvedExtractionInput?> TryResolveImplicitLiveAsync(
        string? candidateRoot,
        GameBuild build,
        ExtractionProfile profile,
        HashSet<string> seenRoots,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return null;
        }

        try
        {
            var normalizedRoot = NormalizePath(candidateRoot);
            if (!seenRoots.Add(normalizedRoot))
            {
                return null;
            }

            var input = CreateInput(
                normalizedRoot,
                ExtractionInputSource.Live,
                inputSnapshotId: null,
                profile,
                explicitCandidate: false);
            await _verifier.CaptureAsync(
                input,
                build,
                profile,
                cancellationToken);
            return input;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ExtractionOperationException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static ResolvedExtractionInput CreateInput(
        string rootPath,
        ExtractionInputSource source,
        string? inputSnapshotId,
        ExtractionProfile profile,
        bool explicitCandidate)
    {
        string root;
        try
        {
            root = NormalizePath(rootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw explicitCandidate
                ? new ExtractionOperationException(
                    ExtractionFailureStage.InputResolution,
                    ExtractionFailureCode.LiveInputNotFound,
                    "The explicit game path is missing or invalid.",
                    innerException: exception)
                : exception;
        }

        var unityVersionSource = profile.UnityVersionSources
            .Select(relativePath => Path.GetFullPath(Path.Combine(root, relativePath)))
            .FirstOrDefault(File.Exists);
        if (unityVersionSource is null)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputResolution,
                ExtractionFailureCode.LiveInputNotFound,
                explicitCandidate
                    ? "The explicit game path is missing a required Unity version source."
                    : "The game input is missing a required Unity version source.");
        }

        return new ResolvedExtractionInput(
            source,
            root,
            Path.Combine(root, "GameAssembly.dll"),
            Path.Combine(
                root,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat"),
            Path.Combine(root, "Schedule I.exe"),
            unityVersionSource,
            inputSnapshotId);
    }

    private static void ValidateStoredSnapshot(InputSnapshot snapshot, GameBuild build)
    {
        if (!snapshot.ReplayVerified ||
            !string.Equals(snapshot.BuildId, build.BuildId, StringComparison.Ordinal) ||
            !string.Equals(
                InputManifestFingerprint.Create(snapshot.Manifest),
                snapshot.ManifestDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The archived input manifest is invalid.");
        }

        var assembly = snapshot.Manifest.Entries.SingleOrDefault(
            entry => entry.Role == "gameAssembly");
        var metadata = snapshot.Manifest.Entries.SingleOrDefault(
            entry => entry.Role == "globalMetadata");
        if (assembly?.Sha256 != build.GameAssemblySha256 ||
            metadata?.Sha256 != build.MetadataSha256)
        {
            throw new InvalidDataException(
                "The archived input manifest does not match the selected build.");
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
