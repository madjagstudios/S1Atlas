using S1Atlas.Core.Hashing;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ManagedToolInstaller : IToolInstaller
{
    private readonly string _toolsRoot;
    private readonly string _stagingRoot;
    private readonly string _quarantineRoot;
    private readonly ManagedToolInstallationValidator _validator;
    private readonly ToolDownloadClient _downloadClient;
    private readonly ToolPackageVerifier _packageVerifier;
    private readonly SafeToolPackageInstaller _packageInstaller;
    private readonly ToolInstallationDocumentStore _documentStore;
    private readonly ToolProbeRunner _probeRunner;
    private readonly IFileHasher _fileHasher;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, string> _movePath;

    public ManagedToolInstaller(
        string toolsRoot,
        string stagingRoot,
        string quarantineRoot,
        ManagedToolInstallationValidator validator,
        ToolDownloadClient downloadClient,
        ToolPackageVerifier packageVerifier,
        SafeToolPackageInstaller packageInstaller,
        ToolInstallationDocumentStore documentStore,
        ToolProbeRunner probeRunner,
        IFileHasher fileHasher,
        TimeProvider? timeProvider = null)
        : this(
            toolsRoot,
            stagingRoot,
            quarantineRoot,
            validator,
            downloadClient,
            packageVerifier,
            packageInstaller,
            documentStore,
            probeRunner,
            fileHasher,
            timeProvider,
            MoveExistingPath)
    {
    }

    internal ManagedToolInstaller(
        string toolsRoot,
        string stagingRoot,
        string quarantineRoot,
        ManagedToolInstallationValidator validator,
        ToolDownloadClient downloadClient,
        ToolPackageVerifier packageVerifier,
        SafeToolPackageInstaller packageInstaller,
        ToolInstallationDocumentStore documentStore,
        ToolProbeRunner probeRunner,
        IFileHasher fileHasher,
        TimeProvider? timeProvider,
        Action<string, string>? movePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentNullException.ThrowIfNull(packageVerifier);
        ArgumentNullException.ThrowIfNull(packageInstaller);
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(probeRunner);
        ArgumentNullException.ThrowIfNull(fileHasher);

        _toolsRoot = Path.GetFullPath(toolsRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
        _validator = validator;
        _downloadClient = downloadClient;
        _packageVerifier = packageVerifier;
        _packageInstaller = packageInstaller;
        _documentStore = documentStore;
        _probeRunner = probeRunner;
        _fileHasher = fileHasher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _movePath = movePath ?? MoveExistingPath;
    }

    public async Task<ManagedToolInstallOutcome> InstallAsync(
        ResolvedToolDefinition definition,
        bool repair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        var current = await _validator.InspectAtRootAsync(
            definition,
            finalRoot,
            cancellationToken);
        if (current.Status == ToolInstallationStatus.Verified &&
            current.Installation is not null)
        {
            return new ManagedToolInstallOutcome(
                current.Installation,
                WasAlreadyVerified: true,
                Repaired: false,
                QuarantinePath: null);
        }

        if (current.Status != ToolInstallationStatus.NotInstalled && !repair)
        {
            throw new ToolOperationException(
                "ToolRepairRequired",
                $"The existing {definition.Definition.DisplayName} installation " +
                $"is {current.Status} and requires explicit repair.");
        }

        var replacingExisting = current.Status != ToolInstallationStatus.NotInstalled;
        var ownedStagingRoot = ToolPathPolicy.CreateStagingPath(
            _stagingRoot,
            definition.Definition);
        var packageDirectory = ToolPathPolicy.ResolveContainedRelativePath(
            ownedStagingRoot,
            "package");
        var packagePath = ToolPathPolicy.ResolveContainedRelativePath(
            packageDirectory,
            definition.Definition.Package.AssetName);
        var stagedInstallRoot = ToolPathPolicy.ResolveContainedRelativePath(
            ownedStagingRoot,
            "install");
        string? quarantinePath = null;

        try
        {
            await _downloadClient.DownloadAsync(
                definition.Definition.Package.SourceUri,
                packagePath,
                definition.Definition.Package.Limits.MaximumDownloadBytes,
                cancellationToken);
            var verifiedPackage = await _packageVerifier.VerifyAsync(
                packagePath,
                definition.Definition.Package,
                cancellationToken);
            var materialized = await _packageInstaller.MaterializeAsync(
                definition,
                verifiedPackage,
                stagedInstallRoot,
                cancellationToken);
            var executableSha256 = await _fileHasher.ComputeSha256Async(
                materialized.ExecutablePath,
                cancellationToken);
            var probeResults = await RunRequiredProbesAsync(
                definition,
                materialized,
                cancellationToken);

            if (replacingExisting)
            {
                quarantinePath = ToolPathPolicy.CreateQuarantinePath(
                    _quarantineRoot,
                    definition.Definition,
                    _timeProvider.GetUtcNow());
            }

            var installedAtUtc = _timeProvider.GetUtcNow();
            var stagedInstallation = new ManagedToolInstallation(
                SchemaVersion: 1,
                ToolId: definition.Definition.ToolId,
                DisplayName: definition.Definition.DisplayName,
                Version: definition.Definition.Version,
                Platform: definition.Definition.Platform,
                DefinitionDigest: definition.DefinitionDigest,
                PackageSha256: verifiedPackage.Sha256,
                ExecutableSha256: executableSha256,
                RootPath: finalRoot,
                Status: ToolInstallationStatus.Verified,
                InstalledAtUtc: installedAtUtc,
                LastVerifiedAtUtc: installedAtUtc,
                ProbeResults: probeResults,
                ReplacedInstallationPath: quarantinePath);
            await _documentStore.WriteAsync(
                stagedInstallRoot,
                definition,
                stagedInstallation,
                cancellationToken);

            var stagedStatus = await _validator.InspectAtRootAsync(
                definition,
                stagedInstallRoot,
                cancellationToken);
            RequireVerified(stagedStatus, "staged");

            Promote(
                stagedInstallRoot,
                finalRoot,
                replacingExisting,
                quarantinePath);

            var finalStatus = await _validator.InspectAtRootAsync(
                definition,
                finalRoot,
                cancellationToken);
            var finalInstallation = RequireVerified(finalStatus, "promoted");
            return new ManagedToolInstallOutcome(
                finalInstallation,
                WasAlreadyVerified: false,
                Repaired: replacingExisting,
                QuarantinePath: quarantinePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolOperationException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedInstallationFailure(exception))
        {
            throw Failure(
                $"The managed tool installation failed: {exception.Message}",
                exception);
        }
        finally
        {
            DeleteFileBestEffort(packagePath);
            DeleteOwnedStagingDirectoryBestEffort(ownedStagingRoot);
        }
    }

    private async Task<IReadOnlyList<ToolProbeResult>> RunRequiredProbesAsync(
        ResolvedToolDefinition definition,
        MaterializedToolPackage materialized,
        CancellationToken cancellationToken)
    {
        var results = new List<ToolProbeResult>(
            definition.Definition.Probes.Count);
        foreach (var probe in definition.Definition.Probes)
        {
            var result = await _probeRunner.RunAsync(
                materialized.ExecutablePath,
                materialized.InstallRoot,
                probe,
                cancellationToken);
            results.Add(result);
            if (!result.Succeeded)
            {
                throw new ToolOperationException(
                    "ToolProbeFailed",
                    result.FailureMessage ??
                    $"Capability probe '{probe.ProbeId}' failed.");
            }
        }

        return results;
    }

    private void Promote(
        string stagedInstallRoot,
        string finalRoot,
        bool replacingExisting,
        string? quarantinePath)
    {
        var finalParent = Path.GetDirectoryName(finalRoot) ??
            throw Failure("The managed tool installation root has no parent directory.");
        Directory.CreateDirectory(finalParent);
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(
            _toolsRoot,
            finalParent);

        if (!replacingExisting)
        {
            TryMoveForPromotion(stagedInstallRoot, finalRoot);
            return;
        }

        if (quarantinePath is null)
        {
            throw Failure("A repair promotion has no quarantine path.");
        }

        var quarantineParent = Path.GetDirectoryName(quarantinePath) ??
            throw Failure("The managed tool quarantine path has no parent directory.");
        Directory.CreateDirectory(quarantineParent);
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(
            _toolsRoot,
            quarantineParent);
        TryMoveForPromotion(finalRoot, quarantinePath);

        try
        {
            TryMoveForPromotion(stagedInstallRoot, finalRoot);
        }
        catch (ToolOperationException promotionException)
        {
            RestoreQuarantinedInstallationBestEffort(quarantinePath, finalRoot);
            throw new ToolOperationException(
                "ToolInstallationFailed",
                "The replacement installation could not be promoted. " +
                "Restoration of the prior installation was attempted.",
                promotionException);
        }
    }

    private void TryMoveForPromotion(string source, string destination)
    {
        try
        {
            _movePath(source, destination);
        }
        catch (Exception exception) when (IsExpectedInstallationFailure(exception))
        {
            throw Failure(
                $"The managed tool path '{source}' could not be moved to " +
                $"'{destination}': {exception.Message}",
                exception);
        }
    }

    private void RestoreQuarantinedInstallationBestEffort(
        string quarantinePath,
        string finalRoot)
    {
        try
        {
            if (!PathExists(finalRoot) && PathExists(quarantinePath))
            {
                _movePath(quarantinePath, finalRoot);
            }
        }
        catch (Exception exception) when (IsExpectedInstallationFailure(exception))
        {
        }
    }

    private static ManagedToolInstallation RequireVerified(
        ManagedToolStatus status,
        string description)
    {
        if (status.Status != ToolInstallationStatus.Verified ||
            status.Installation is null)
        {
            throw Failure(
                $"The {description} managed tool installation did not verify: " +
                $"{status.DiagnosticMessage ?? status.Status.ToString()}.");
        }

        return status.Installation;
    }

    private static void MoveExistingPath(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            return;
        }

        if (File.Exists(source))
        {
            File.Move(source, destination);
            return;
        }

        throw new FileNotFoundException(
            "The managed tool path to move does not exist.",
            source);
    }

    private static bool PathExists(string path) =>
        Directory.Exists(path) || File.Exists(path);

    private static bool IsExpectedInstallationFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException;

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DeleteOwnedStagingDirectoryBestEffort(string path)
    {
        try
        {
            ToolPathPolicy.EnsureNoReparsePointInExistingPath(
                _stagingRoot,
                path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ToolOperationException)
        {
        }
    }

    private static ToolOperationException Failure(
        string message,
        Exception? innerException = null) =>
        new("ToolInstallationFailed", message, innerException);
}
