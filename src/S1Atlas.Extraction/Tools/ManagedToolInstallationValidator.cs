using S1Atlas.Core.Hashing;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ManagedToolInstallationValidator
{
    private readonly string _toolsRoot;
    private readonly ToolInstallationDocumentStore _documentStore;
    private readonly Func<
        string,
        string,
        ToolProbeDefinition,
        CancellationToken,
        Task<ToolProbeResult>> _probeExecutor;
    private readonly IFileHasher _fileHasher;
    private readonly TimeProvider _timeProvider;

    public ManagedToolInstallationValidator(
        string toolsRoot,
        ToolInstallationDocumentStore documentStore,
        ToolProbeRunner probeRunner,
        IFileHasher fileHasher,
        TimeProvider? timeProvider = null)
        : this(
            toolsRoot,
            documentStore,
            probeRunner.RunAsync,
            fileHasher,
            timeProvider)
    {
    }

    internal ManagedToolInstallationValidator(
        string toolsRoot,
        ToolInstallationDocumentStore documentStore,
        Func<
            string,
            string,
            ToolProbeDefinition,
            CancellationToken,
            Task<ToolProbeResult>> probeExecutor,
        IFileHasher fileHasher,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(probeExecutor);
        ArgumentNullException.ThrowIfNull(fileHasher);

        _toolsRoot = Path.GetFullPath(toolsRoot);
        _documentStore = documentStore;
        _probeExecutor = probeExecutor;
        _fileHasher = fileHasher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ManagedToolStatus> InspectAsync(
        ResolvedToolDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        return InspectAtRootAsync(definition, installRoot, cancellationToken);
    }

    internal async Task<ManagedToolStatus> InspectAtRootAsync(
        ResolvedToolDefinition definition,
        string installRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var fullInstallRoot = Path.GetFullPath(installRoot);
        try
        {
            ToolPathPolicy.EnsureNoReparsePointInExistingPath(
                _toolsRoot,
                fullInstallRoot);
        }
        catch (ToolOperationException exception)
        {
            return Incomplete(definition, exception.Message);
        }

        if (!PathExists(fullInstallRoot))
        {
            return new ManagedToolStatus(
                definition,
                ToolInstallationStatus.NotInstalled,
                Installation: null,
                DiagnosticCode: "ToolNotInstalled",
                DiagnosticMessage:
                    $"{definition.Definition.DisplayName} is not installed.");
        }

        if (!IsNormalDirectory(fullInstallRoot))
        {
            return Incomplete(
                definition,
                "The managed tool installation root is not a regular directory.");
        }

        var documents = await _documentStore.TryReadAsync(
            fullInstallRoot,
            cancellationToken);
        if (documents is null)
        {
            return Incomplete(
                definition,
                "The managed tool installation documents are missing or malformed.");
        }

        var localDefinition = documents.Value.Definition;
        var storedInstallation = documents.Value.Installation;
        if (!MatchesDefinition(
                definition,
                localDefinition,
                storedInstallation))
        {
            return new ManagedToolStatus(
                definition,
                ToolInstallationStatus.DefinitionMismatch,
                WithState(
                    storedInstallation,
                    fullInstallRoot,
                    ToolInstallationStatus.DefinitionMismatch),
                DiagnosticCode: "ToolDefinitionMismatch",
                DiagnosticMessage:
                    "The local managed tool definition does not match the committed definition.");
        }

        string executablePath;
        try
        {
            executablePath = ToolPathPolicy.ResolveContainedRelativePath(
                fullInstallRoot,
                definition.Definition.Package.ExecutableRelativePath);
            ToolPathPolicy.EnsureNoReparsePointInExistingPath(
                fullInstallRoot,
                executablePath);
        }
        catch (ToolOperationException exception)
        {
            return Incomplete(definition, exception.Message, storedInstallation);
        }

        if (!IsRegularFile(executablePath))
        {
            return Incomplete(
                definition,
                "The managed tool executable is missing or is not a regular file.",
                storedInstallation);
        }

        var executableSha256 = await _fileHasher.ComputeSha256Async(
            executablePath,
            cancellationToken);
        var expectedExecutableSha256 = definition.Definition.Package.Kind ==
            ToolPackageKind.SingleFile
                ? definition.Definition.Package.Sha256
                : storedInstallation.ExecutableSha256;
        if (!string.Equals(
                executableSha256,
                expectedExecutableSha256,
                StringComparison.Ordinal))
        {
            var corruptInstallation = WithState(
                storedInstallation,
                fullInstallRoot,
                ToolInstallationStatus.Corrupt,
                executableSha256: executableSha256);
            return new ManagedToolStatus(
                definition,
                ToolInstallationStatus.Corrupt,
                corruptInstallation,
                DiagnosticCode: "ToolExecutableChecksumMismatch",
                DiagnosticMessage:
                    "The managed tool executable checksum does not match its verified installation record.");
        }

        var probeResults = new List<ToolProbeResult>(
            definition.Definition.Probes.Count);
        foreach (var probe in definition.Definition.Probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _probeExecutor(
                executablePath,
                fullInstallRoot,
                probe,
                cancellationToken);
            probeResults.Add(result);
        }

        var verificationTime = _timeProvider.GetUtcNow();
        var status = probeResults.All(result => result.Succeeded)
            ? ToolInstallationStatus.Verified
            : ToolInstallationStatus.ProbeFailed;
        var installation = storedInstallation with
        {
            SchemaVersion = 1,
            ToolId = definition.Definition.ToolId,
            DisplayName = definition.Definition.DisplayName,
            Version = definition.Definition.Version,
            Platform = definition.Definition.Platform,
            DefinitionDigest = definition.DefinitionDigest,
            PackageSha256 = definition.Definition.Package.Sha256,
            ExecutableSha256 = executableSha256,
            RootPath = fullInstallRoot,
            Status = status,
            LastVerifiedAtUtc = verificationTime,
            ProbeResults = probeResults
        };

        return status == ToolInstallationStatus.Verified
            ? new ManagedToolStatus(
                definition,
                status,
                installation,
                DiagnosticCode: null,
                DiagnosticMessage: null)
            : new ManagedToolStatus(
                definition,
                status,
                installation,
                DiagnosticCode: "ToolProbeFailed",
                DiagnosticMessage:
                    "One or more managed tool capability probes failed.");
    }

    private static bool MatchesDefinition(
        ResolvedToolDefinition expected,
        ResolvedToolDefinition local,
        ManagedToolInstallation installation) =>
        string.Equals(
            expected.DefinitionDigest,
            local.DefinitionDigest,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.DefinitionDigest,
            installation.DefinitionDigest,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.Definition.ToolId,
            installation.ToolId,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.Definition.DisplayName,
            installation.DisplayName,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.Definition.Version,
            installation.Version,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.Definition.Platform,
            installation.Platform,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.Definition.Package.Sha256,
            installation.PackageSha256,
            StringComparison.Ordinal);

    private static ManagedToolStatus Incomplete(
        ResolvedToolDefinition definition,
        string message,
        ManagedToolInstallation? installation = null) =>
        new(
            definition,
            ToolInstallationStatus.Incomplete,
            installation is null
                ? null
                : WithState(
                    installation,
                    installation.RootPath,
                    ToolInstallationStatus.Incomplete),
            DiagnosticCode: "ToolInstallationIncomplete",
            DiagnosticMessage: message);

    private static ManagedToolInstallation WithState(
        ManagedToolInstallation installation,
        string rootPath,
        ToolInstallationStatus status,
        string? executableSha256 = null) =>
        installation with
        {
            RootPath = Path.GetFullPath(rootPath),
            Status = status,
            ExecutableSha256 = executableSha256 ?? installation.ExecutableSha256
        };

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsNormalDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
