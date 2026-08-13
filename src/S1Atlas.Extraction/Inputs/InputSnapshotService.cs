using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Extraction.Inputs;

internal sealed class InputSnapshotService
{
    private const string GameRootDirectoryName = "game-root";

    private readonly string _inputsRoot;
    private readonly string _stagingRoot;
    private readonly IFileHasher _fileHasher;
    private readonly TimeProvider _timeProvider;
    private readonly IExtractionRepository _repository;
    private readonly InputSnapshotDocumentStore _documentStore;

    public InputSnapshotService(
        string inputsRoot,
        string stagingRoot,
        IFileHasher fileHasher,
        TimeProvider timeProvider,
        IExtractionRepository repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        _inputsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputsRoot));
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        if (!PathSafety.IsContained(_inputsRoot, _stagingRoot) ||
            !PathSafety.PathsEqual(Path.GetDirectoryName(_stagingRoot)!, _inputsRoot))
        {
            throw new ArgumentException(
                "The staging root must be a direct child of the build inputs root.",
                nameof(stagingRoot));
        }

        _documentStore = new InputSnapshotDocumentStore();
    }

    public async Task<InputSnapshot> CreateAsync(
        ResolvedExtractionInput input,
        GameBuild build,
        ExtractionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        RequireLiveInput(input);
        RequireSafeSegment(build.BuildId, "build ID");
        var declarations = CreateDeclarations(input, profile);
        EnsureDestinationRoots();

        var ownedStagingRoot = Path.Combine(
            _stagingRoot,
            Guid.NewGuid().ToString("N"));
        var stagedGameRoot = Path.Combine(ownedStagingRoot, GameRootDirectoryName);
        var promoted = false;
        try
        {
            Directory.CreateDirectory(stagedGameRoot);
            if (!PathSafety.IsNormalDirectory(ownedStagingRoot) ||
                !PathSafety.IsNormalDirectory(stagedGameRoot))
            {
                throw FilesystemFailure("The owned input staging directory is unsafe.");
            }

            var entries = new List<InputManifestEntry>(declarations.Count);
            long totalBytes = 0;
            foreach (var declaration in declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = await CopyAndVerifyAsync(
                    input.GameRoot,
                    stagedGameRoot,
                    declaration,
                    cancellationToken);
                totalBytes = checked(totalBytes + entry.Size);
                entries.Add(entry);
                cancellationToken.ThrowIfCancellationRequested();
            }

            RequireBuildHashes(entries, build);
            var manifest = new InputManifest(entries
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray());
            var candidate = InputSnapshot.CreateUnverified(
                build.BuildId,
                _inputsRoot,
                manifest,
                _timeProvider.GetUtcNow());

            await _documentStore.WriteAsync(
                ownedStagingRoot,
                candidate,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            InputSnapshot snapshot;
            try
            {
                Directory.Move(ownedStagingRoot, candidate.RootPath);
                promoted = true;
                snapshot = candidate;
            }
            catch (IOException exception) when (Directory.Exists(candidate.RootPath))
            {
                snapshot = await VerifyExistingAsync(
                    candidate,
                    cancellationToken,
                    exception);
                DeleteOwnedStagingDirectory(ownedStagingRoot);
                promoted = true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw FilesystemFailure(
                    "The input snapshot could not be atomically promoted.",
                    exception);
            }

            try
            {
                await _repository.SaveInputSnapshotAsync(snapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ExtractionOperationException(
                    ExtractionFailureStage.DatabasePromotion,
                    ExtractionFailureCode.DatabasePromotionFailed,
                    $"Input snapshot '{snapshot.InputSnapshotId}' was promoted but could not be registered.",
                    innerException: exception);
            }

            // Return the canonical persisted record rather than the freshly built
            // candidate: when identical snapshot bytes were recreated over an existing
            // row, the save is an idempotent no-op and the persisted record may already
            // be replay-certified. Reloading preserves that certification state.
            return await _repository.GetInputSnapshotAsync(
                snapshot.InputSnapshotId,
                cancellationToken) ?? throw new ExtractionOperationException(
                    ExtractionFailureStage.DatabasePromotion,
                    ExtractionFailureCode.DatabasePromotionFailed,
                    $"Input snapshot '{snapshot.InputSnapshotId}' was registered but " +
                    "could not be reloaded from the database.");
        }
        catch (OverflowException exception)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputSnapshotCreation,
                ExtractionFailureCode.IntegrityMismatch,
                "The total input snapshot size overflowed the supported range.",
                innerException: exception);
        }
        finally
        {
            if (!promoted)
            {
                DeleteOwnedStagingDirectoryBestEffort(ownedStagingRoot);
            }
        }
    }

    private async Task<InputSnapshot> VerifyExistingAsync(
        InputSnapshot candidate,
        CancellationToken cancellationToken,
        IOException promotionException)
    {
        var existing = await _documentStore.TryReadVerifiedAsync(
            candidate.RootPath,
            candidate.BuildId,
            candidate.InputSnapshotId,
            _fileHasher,
            cancellationToken);
        if (existing is null ||
            !string.Equals(
                existing.ManifestDigest,
                candidate.ManifestDigest,
                StringComparison.Ordinal))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputSnapshotCreation,
                ExtractionFailureCode.ArchivedInputInvalid,
                $"Existing input snapshot '{candidate.InputSnapshotId}' conflicts with the staged snapshot.",
                innerException: promotionException);
        }

        return existing;
    }

    private async Task<InputManifestEntry> CopyAndVerifyAsync(
        string sourceRoot,
        string destinationRoot,
        FileDeclaration declaration,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveContainedFile(
            sourceRoot,
            declaration.RelativePath,
            ExtractionFailureCode.LiveInputNotFound);
        if (!PathSafety.TryObserveRegularFile(sourceRoot, sourcePath, out var before))
        {
            throw SourceMissing(declaration.RelativePath);
        }

        string sourceHashBefore;
        try
        {
            sourceHashBefore = await _fileHasher.ComputeSha256Async(
                sourcePath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw SourceChanged(declaration.RelativePath, exception);
        }

        if (!PathSafety.TryObserveRegularFile(sourceRoot, sourcePath, out var afterFirstHash) ||
            !before.IsStableWith(afterFirstHash))
        {
            throw SourceChanged(declaration.RelativePath);
        }

        var destinationPath = ResolveContainedFile(
            destinationRoot,
            declaration.RelativePath,
            ExtractionFailureCode.FilesystemPromotionFailed);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        if (!PathSafety.IsNormalDirectory(destinationDirectory) ||
            !PathSafety.IsContained(destinationRoot, destinationPath))
        {
            throw FilesystemFailure(
                $"Input snapshot destination '{declaration.RelativePath}' is unsafe.");
        }

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw FilesystemFailure(
                $"Input snapshot file '{declaration.RelativePath}' could not be copied.",
                exception);
        }

        File.SetLastWriteTimeUtc(destinationPath, before.LastWriteUtc.UtcDateTime);
        if (!PathSafety.TryObserveRegularFile(destinationRoot, destinationPath, out var destinationInfo) ||
            destinationInfo.Length != before.Length)
        {
            throw IntegrityFailure(declaration.RelativePath);
        }

        var destinationHash = await _fileHasher.ComputeSha256Async(
            destinationPath,
            cancellationToken);
        if (!PathSafety.TryObserveRegularFile(sourceRoot, sourcePath, out var beforeFinalHash))
        {
            throw SourceChanged(declaration.RelativePath);
        }

        var sourceHashAfter = await _fileHasher.ComputeSha256Async(
            sourcePath,
            cancellationToken);
        if (!PathSafety.TryObserveRegularFile(sourceRoot, sourcePath, out var afterFinalHash) ||
            !beforeFinalHash.IsStableWith(afterFinalHash) ||
            !before.IsStableWith(afterFinalHash) ||
            !string.Equals(sourceHashBefore, destinationHash, StringComparison.Ordinal) ||
            !string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal))
        {
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal) ||
                !before.IsStableWith(afterFinalHash))
            {
                throw SourceChanged(declaration.RelativePath);
            }

            throw IntegrityFailure(declaration.RelativePath);
        }

        return new InputManifestEntry(
            declaration.RelativePath,
            declaration.Role,
            before.Length,
            sourceHashBefore,
            before.LastWriteUtc);
    }

    private static IReadOnlyList<FileDeclaration> CreateDeclarations(
        ResolvedExtractionInput input,
        ExtractionProfile profile)
    {
        if (!PathSafety.IsNormalDirectory(input.GameRoot))
        {
            throw SourceMissing("game root");
        }

        var root = Path.GetFullPath(input.GameRoot);
        var declarations = new List<FileDeclaration>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profile.SnapshotInputs ?? [])
        {
            if (item is null)
            {
                throw ProfileFailure("The extraction profile contains an empty snapshot input.");
            }

            var path = NormalizeRelativePath(root, item.RelativePath);
            if (!allPaths.Add(path))
            {
                throw ProfileFailure(
                    $"Extraction profile input path '{path}' collides ignoring case.");
            }

            if (string.IsNullOrWhiteSpace(item.Role))
            {
                throw ProfileFailure("The extraction profile contains an empty snapshot role.");
            }

            declarations.Add(new FileDeclaration(path, item.Role));
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gameAssembly"] = input.GameAssemblyPath,
            ["globalMetadata"] = input.GlobalMetadataPath,
            ["executableSupport"] = input.ExecutablePath
        };
        if (declarations.Count != expected.Count)
        {
            throw ProfileFailure("The extraction profile must declare exactly the required snapshot inputs.");
        }

        foreach (var pair in expected)
        {
            var declaration = declarations.SingleOrDefault(item =>
                string.Equals(item.Role, pair.Key, StringComparison.Ordinal));
            if (declaration is null ||
                !PathSafety.PathsEqual(
                    Path.Combine(root, declaration.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    Path.GetFullPath(pair.Value)))
            {
                throw ProfileFailure(
                    $"Extraction profile role '{pair.Key}' does not match the resolved live input.");
            }
        }

        string? selectedUnityPath = null;
        foreach (var source in profile.UnityVersionSources ?? [])
        {
            var path = NormalizeRelativePath(root, source);
            if (!allPaths.Add(path))
            {
                throw ProfileFailure(
                    $"Extraction profile input path '{path}' collides ignoring case.");
            }

            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            if (!PathSafety.TryObserveRegularFile(root, fullPath, out _))
            {
                throw SourceMissing(path);
            }

            selectedUnityPath = path;
            break;
        }

        if (selectedUnityPath is null ||
            !PathSafety.PathsEqual(
                Path.Combine(root, selectedUnityPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.GetFullPath(input.UnityVersionSourcePath)))
        {
            throw SourceMissing("Unity version source");
        }

        declarations.Add(new FileDeclaration(selectedUnityPath, "unityVersionSource"));
        return declarations
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeRelativePath(string root, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Contains(':') ||
                relativePath.Any(char.IsControl))
            {
                throw ProfileFailure("The extraction profile contains an unsafe snapshot path.");
            }

            var normalized = relativePath.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.None);
            if (segments.Any(segment =>
                    string.IsNullOrEmpty(segment) || segment is "." or ".."))
            {
                throw ProfileFailure("The extraction profile contains an unsafe snapshot path.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!PathSafety.IsContained(root, fullPath))
            {
                throw ProfileFailure("The extraction profile snapshot path escapes the game root.");
            }

            return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw ProfileFailure("The extraction profile contains an unsafe snapshot path.", exception);
        }
    }

    private void EnsureDestinationRoots()
    {
        try
        {
            Directory.CreateDirectory(_inputsRoot);
            if (!PathSafety.IsNormalDirectory(_inputsRoot))
            {
                throw FilesystemFailure("The build inputs root is unsafe.");
            }

            if (Directory.Exists(_stagingRoot) && !PathSafety.IsNormalDirectory(_stagingRoot))
            {
                throw FilesystemFailure("The input staging root is unsafe.");
            }

            Directory.CreateDirectory(_stagingRoot);
            if (!PathSafety.IsNormalDirectory(_stagingRoot))
            {
                throw FilesystemFailure("The input staging root is unsafe.");
            }
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw FilesystemFailure("The input snapshot roots could not be prepared.", exception);
        }
    }

    private static string ResolveContainedFile(
        string root,
        string relativePath,
        ExtractionFailureCode failureCode)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathSafety.IsContained(fullRoot, path))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputSnapshotCreation,
                failureCode,
                $"Input snapshot path '{relativePath}' escapes its owned root.");
        }

        return path;
    }

    private static void RequireLiveInput(ResolvedExtractionInput input)
    {
        if (input.Source != ExtractionInputSource.Live || input.InputSnapshotId is not null)
        {
            throw ProfileFailure("Only verified live input can be archived as a new snapshot.");
        }
    }

    private static void RequireSafeSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            Path.GetFileName(value) != value ||
            value.Contains(':') ||
            value.Any(char.IsControl))
        {
            throw ProfileFailure($"The {description} is not a safe path segment.");
        }
    }

    private static void RequireBuildHashes(
        IReadOnlyList<InputManifestEntry> entries,
        GameBuild build)
    {
        var assembly = entries.Single(item => item.Role == "gameAssembly");
        var metadata = entries.Single(item => item.Role == "globalMetadata");
        if (!string.Equals(
                assembly.Sha256,
                build.GameAssemblySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.Sha256,
                build.MetadataSha256,
                StringComparison.Ordinal))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.InputSnapshotCreation,
                ExtractionFailureCode.BuildInputMismatch,
                "The copied live input does not belong to the selected Atlas build.");
        }
    }

    private void DeleteOwnedStagingDirectoryBestEffort(string path)
    {
        try
        {
            DeleteOwnedStagingDirectory(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private void DeleteOwnedStagingDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!PathSafety.IsContained(_stagingRoot, fullPath) ||
            !PathSafety.PathsEqual(Path.GetDirectoryName(fullPath)!, _stagingRoot) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _))
        {
            throw new InvalidOperationException("Refusing to delete an unowned staging directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            return;
        }

        if (!PathSafety.IsNormalDirectory(fullPath))
        {
            throw new InvalidOperationException("Refusing to delete staging through a reparse point.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static ExtractionOperationException SourceMissing(string description) =>
        new(
            ExtractionFailureStage.InputSnapshotCreation,
            ExtractionFailureCode.LiveInputNotFound,
            $"Required snapshot input '{description}' is missing or unsafe.");

    private static ExtractionOperationException SourceChanged(
        string relativePath,
        Exception? innerException = null) =>
        new(
            ExtractionFailureStage.InputSnapshotCreation,
            ExtractionFailureCode.BuildInputMismatch,
            $"Snapshot input '{relativePath}' changed while it was copied.",
            innerException: innerException);

    private static ExtractionOperationException IntegrityFailure(string relativePath) =>
        new(
            ExtractionFailureStage.InputSnapshotCreation,
            ExtractionFailureCode.IntegrityMismatch,
            $"Copied snapshot input '{relativePath}' failed destination verification.");

    private static ExtractionOperationException ProfileFailure(
        string message,
        Exception? innerException = null) =>
        new(
            ExtractionFailureStage.InputSnapshotCreation,
            ExtractionFailureCode.BuildInputMismatch,
            message,
            innerException: innerException);

    private static ExtractionOperationException FilesystemFailure(
        string message,
        Exception? innerException = null) =>
        new(
            ExtractionFailureStage.InputSnapshotCreation,
            ExtractionFailureCode.FilesystemPromotionFailed,
            message,
            innerException: innerException);

    private sealed record FileDeclaration(string RelativePath, string Role);
}
