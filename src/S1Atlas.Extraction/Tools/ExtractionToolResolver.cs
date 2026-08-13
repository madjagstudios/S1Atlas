using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed record ResolvedExtractionTool(
    ResolvedToolDefinition Definition,
    ToolInstance Instance,
    string ExecutablePath,
    IReadOnlyList<ToolProbeResult> ProbeResults);

internal sealed class ExtractionToolResolver
{
    private const string ToolId = "cpp2il";

    private readonly IToolDefinitionProvider _definitionProvider;
    private readonly ManagedToolInstallationValidator _managedValidator;
    private readonly Func<
        string,
        string,
        ToolProbeDefinition,
        CancellationToken,
        Task<ToolProbeResult>> _probeExecutor;
    private readonly IFileHasher _fileHasher;
    private readonly IToolRepository _repository;
    private readonly string _toolsRoot;
    private readonly string _platform;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, FileAttributes> _getFileAttributes;

    public ExtractionToolResolver(
        IToolDefinitionProvider definitionProvider,
        ManagedToolInstallationValidator managedValidator,
        ToolProbeRunner probeRunner,
        IFileHasher fileHasher,
        IToolRepository repository,
        string toolsRoot,
        string platform,
        TimeProvider? timeProvider = null)
        : this(
            definitionProvider,
            managedValidator,
            GetProbeExecutor(probeRunner),
            fileHasher,
            repository,
            toolsRoot,
            platform,
            timeProvider)
    {
    }

    internal ExtractionToolResolver(
        IToolDefinitionProvider definitionProvider,
        ManagedToolInstallationValidator managedValidator,
        Func<
            string,
            string,
            ToolProbeDefinition,
            CancellationToken,
            Task<ToolProbeResult>> probeExecutor,
        IFileHasher fileHasher,
        IToolRepository repository,
        string toolsRoot,
        string platform,
        TimeProvider? timeProvider = null,
        Func<string, FileAttributes>? getFileAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(definitionProvider);
        ArgumentNullException.ThrowIfNull(managedValidator);
        ArgumentNullException.ThrowIfNull(probeExecutor);
        ArgumentNullException.ThrowIfNull(fileHasher);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        _definitionProvider = definitionProvider;
        _managedValidator = managedValidator;
        _probeExecutor = probeExecutor;
        _fileHasher = fileHasher;
        _repository = repository;
        _toolsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolsRoot));
        _platform = platform;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getFileAttributes = getFileAttributes ?? File.GetAttributes;
    }

    public async Task<ResolvedExtractionTool> ResolveAsync(
        string? customExecutablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = _definitionProvider.GetRequired(ToolId, _platform);
        return customExecutablePath is null
            ? await ResolveManagedAsync(definition, cancellationToken)
            : await ResolveCustomAsync(
                definition,
                customExecutablePath,
                cancellationToken);
    }

    private async Task<ResolvedExtractionTool> ResolveManagedAsync(
        ResolvedToolDefinition definition,
        CancellationToken cancellationToken)
    {
        ManagedToolStatus status;
        try
        {
            status = await _managedValidator.InspectAsync(
                definition,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ToolOperationException(
                "ToolChecksumMismatch",
                "The managed Cpp2IL executable could not be freshly hashed.",
                exception);
        }

        if (status.Status != ToolInstallationStatus.Verified ||
            status.Installation is null)
        {
            throw ManagedResolutionFailure(status);
        }

        var instance = ManagedToolInstanceFactory.Create(
            definition,
            status.Installation);
        await _repository.SaveToolInstanceAsync(instance, cancellationToken);
        return new ResolvedExtractionTool(
            definition,
            instance,
            instance.ObservedPath,
            status.Installation.ProbeResults);
    }

    private async Task<ResolvedExtractionTool> ResolveCustomAsync(
        ResolvedToolDefinition definition,
        string customExecutablePath,
        CancellationToken cancellationToken)
    {
        var executablePath = ValidateCustomExecutablePath(customExecutablePath);
        string executableSha256;
        try
        {
            executableSha256 = await _fileHasher.ComputeSha256Async(
                executablePath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ToolOperationException(
                "ToolChecksumMismatch",
                "The custom Cpp2IL executable could not be hashed.",
                exception);
        }

        var workingDirectory = Path.GetDirectoryName(executablePath) ??
            throw CustomPathFailure(
                "The custom Cpp2IL executable has no parent directory.");
        var probeResults = new List<ToolProbeResult>(
            definition.Definition.Probes.Count);
        try
        {
            foreach (var probe in definition.Definition.Probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                probeResults.Add(await _probeExecutor(
                    executablePath,
                    workingDirectory,
                    probe,
                    cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ToolOperationException(
                "ToolProbeFailed",
                "The custom Cpp2IL capability probe could not complete.",
                exception);
        }

        if (probeResults.Any(result => !result.Succeeded))
        {
            throw new ToolOperationException(
                "ToolProbeFailed",
                "One or more custom Cpp2IL capability probes failed.");
        }

        var verificationTime = _timeProvider.GetUtcNow();
        var instance = new ToolInstance(
            ToolInstanceId.Create(
                definition.Definition.ToolId,
                executableSha256,
                definition.Definition.Platform,
                ToolTrustLevel.CustomOverride),
            definition.Definition.ToolId,
            VersionLabel: null,
            definition.Definition.Platform,
            ToolTrustLevel.CustomOverride,
            DefinitionDigest: null,
            PackageSha256: null,
            executableSha256,
            executablePath,
            verificationTime,
            verificationTime,
            ToolInstallationStatus.Verified);
        await _repository.SaveToolInstanceAsync(instance, cancellationToken);
        return new ResolvedExtractionTool(
            definition,
            instance,
            executablePath,
            probeResults);
    }

    private string ValidateCustomExecutablePath(string customExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(customExecutablePath))
        {
            throw CustomPathFailure(
                "The custom Cpp2IL executable path is empty.");
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(customExecutablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            throw CustomPathFailure(
                "The custom Cpp2IL executable path is invalid.",
                exception);
        }

        if (IsSameOrDescendant(_toolsRoot, executablePath))
        {
            throw CustomPathFailure(
                "A custom Cpp2IL override cannot be located under Atlas managed tools.");
        }

        var root = Path.GetPathRoot(executablePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw CustomPathFailure(
                "The custom Cpp2IL executable path has no volume root.");
        }

        var current = Path.TrimEndingDirectorySeparator(root);
        CheckCustomPathSegment(current);
        var relativePath = Path.GetRelativePath(root, executablePath);
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckCustomPathSegment(current);
        }

        FileAttributes attributes;
        try
        {
            attributes = _getFileAttributes(executablePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw CustomPathFailure(
                "The custom Cpp2IL executable does not exist or cannot be inspected.",
                exception);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw CustomPathFailure(
                "The custom Cpp2IL executable must be a regular file.");
        }

        return executablePath;
    }

    private void CheckCustomPathSegment(string path)
    {
        try
        {
            var attributes = _getFileAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw CustomPathFailure(
                    "The custom Cpp2IL path crosses a reparse point.");
            }
        }
        catch (ToolOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw CustomPathFailure(
                "The custom Cpp2IL path does not exist or cannot be inspected.",
                exception);
        }
    }

    private static ToolOperationException ManagedResolutionFailure(
        ManagedToolStatus status)
    {
        var (code, guidance) = status.Status switch
        {
            ToolInstallationStatus.NotInstalled => (
                "ToolNotInstalled",
                "Install it with 's1atlas tools install cpp2il'."),
            ToolInstallationStatus.Incomplete => (
                "ToolNotInstalled",
                "Repair it with 's1atlas tools install cpp2il --repair'."),
            ToolInstallationStatus.DefinitionMismatch => (
                "ToolDefinitionInvalid",
                "Repair the managed installation before extraction."),
            ToolInstallationStatus.Corrupt => (
                "ToolChecksumMismatch",
                "Repair the managed installation before extraction."),
            ToolInstallationStatus.ProbeFailed => (
                "ToolProbeFailed",
                "Repair the managed installation before extraction."),
            _ => (
                "ToolNotInstalled",
                "Install it with 's1atlas tools install cpp2il'.")
        };
        var diagnostic = string.IsNullOrWhiteSpace(status.DiagnosticMessage)
            ? "The managed Cpp2IL installation is not verified."
            : status.DiagnosticMessage;
        return new ToolOperationException(code, $"{diagnostic} {guidance}");
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static ToolOperationException CustomPathFailure(
        string message,
        Exception? innerException = null) =>
        new("CustomToolPathInvalid", message, innerException);

    private static Func<
        string,
        string,
        ToolProbeDefinition,
        CancellationToken,
        Task<ToolProbeResult>> GetProbeExecutor(ToolProbeRunner probeRunner)
    {
        ArgumentNullException.ThrowIfNull(probeRunner);
        return probeRunner.RunAsync;
    }
}
