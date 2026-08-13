using System.IO.Compression;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed record MaterializedToolPackage(
    string InstallRoot,
    string ExecutablePath,
    int FileCount,
    long ExpandedBytes);

internal sealed class SafeToolPackageInstaller
{
    private const int BufferSize = 64 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;

    public async Task<MaterializedToolPackage> MaterializeAsync(
        ResolvedToolDefinition definition,
        VerifiedToolPackage package,
        string stagedInstallRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedInstallRoot);

        var fullStagedRoot = Path.GetFullPath(stagedInstallRoot);
        if (Directory.Exists(fullStagedRoot) || File.Exists(fullStagedRoot))
        {
            throw Failure(
                $"The staged tool installation path '{fullStagedRoot}' already exists.");
        }

        try
        {
            return definition.Definition.Package.Kind switch
            {
                ToolPackageKind.SingleFile => await MaterializeSingleFileAsync(
                    definition,
                    package,
                    fullStagedRoot,
                    cancellationToken),
                ToolPackageKind.Archive => await MaterializeZipAsync(
                    definition,
                    package,
                    fullStagedRoot,
                    cancellationToken),
                _ => throw Failure("The tool package kind is not supported.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(fullStagedRoot);
            throw;
        }
        catch (ToolOperationException)
        {
            TryDeleteDirectory(fullStagedRoot);
            throw;
        }
        catch (Exception exception) when (IsExpectedMaterializationFailure(exception))
        {
            TryDeleteDirectory(fullStagedRoot);
            throw Failure(
                $"The verified tool package could not be materialized: {exception.Message}",
                exception);
        }
    }

    private static async Task<MaterializedToolPackage> MaterializeSingleFileAsync(
        ResolvedToolDefinition definition,
        VerifiedToolPackage package,
        string stagedInstallRoot,
        CancellationToken cancellationToken)
    {
        var packageDefinition = definition.Definition.Package;
        if (packageDefinition.ArchiveFormat is not null)
        {
            throw Failure(
                "A single-file tool package cannot declare an archive format.");
        }

        if (package.Size > packageDefinition.Limits.MaximumExpandedBytes)
        {
            throw Failure(
                "The single-file tool package exceeds its maximum expanded size.");
        }

        var executablePath = ToolPathPolicy.ResolveContainedRelativePath(
            stagedInstallRoot,
            packageDefinition.ExecutableRelativePath);
        var executableDirectory = Path.GetDirectoryName(executablePath) ??
            throw Failure("The declared executable path has no parent directory.");

        Directory.CreateDirectory(executableDirectory);
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(
            stagedInstallRoot,
            executableDirectory);

        await using var source = new FileStream(
            package.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            executablePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var copied = await CopyExactAsync(
            source,
            destination,
            cancellationToken);
        if (copied != package.Size)
        {
            throw Failure(
                "The single-file tool package changed while it was being materialized.");
        }

        await destination.FlushAsync(cancellationToken);
        return new MaterializedToolPackage(
            stagedInstallRoot,
            executablePath,
            FileCount: 1,
            ExpandedBytes: copied);
    }

    private static async Task<MaterializedToolPackage> MaterializeZipAsync(
        ResolvedToolDefinition definition,
        VerifiedToolPackage package,
        string stagedInstallRoot,
        CancellationToken cancellationToken)
    {
        var packageDefinition = definition.Definition.Package;
        if (packageDefinition.ArchiveFormat != ToolArchiveFormat.Zip)
        {
            throw Failure("The archive tool package must use ZIP format.");
        }

        await using var packageStream = new FileStream(
            package.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: false);

        var plans = PreflightZip(
            archive,
            stagedInstallRoot,
            packageDefinition);
        var executablePath = ToolPathPolicy.ResolveContainedRelativePath(
            stagedInstallRoot,
            packageDefinition.ExecutableRelativePath);
        var executableRelativePath = NormalizeManifestPath(
            Path.GetRelativePath(stagedInstallRoot, executablePath));
        if (!plans.Any(plan =>
                !plan.IsDirectory &&
                string.Equals(
                    plan.RelativePath,
                    executableRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure(
                "The ZIP package does not contain the declared executable.");
        }

        Directory.CreateDirectory(stagedInstallRoot);
        foreach (var directory in plans
                     .Where(plan => plan.IsDirectory)
                     .OrderBy(plan => plan.RelativePath.Count(character => character == '/'))
                     .ThenBy(plan => plan.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(plan => plan.RelativePath, StringComparer.Ordinal))
        {
            var directoryPath = ToolPathPolicy.ResolveContainedRelativePath(
                stagedInstallRoot,
                directory.RelativePath);
            Directory.CreateDirectory(directoryPath);
            ToolPathPolicy.EnsureNoReparsePointInExistingPath(
                stagedInstallRoot,
                directoryPath);
        }

        long extractedBytes = 0;
        var extractedFiles = 0;
        foreach (var file in plans.Where(plan => !plan.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = ToolPathPolicy.ResolveContainedRelativePath(
                stagedInstallRoot,
                file.RelativePath);
            var parent = Path.GetDirectoryName(destinationPath) ??
                throw Failure("A ZIP entry has no contained parent directory.");
            Directory.CreateDirectory(parent);
            ToolPathPolicy.EnsureNoReparsePointInExistingPath(
                stagedInstallRoot,
                parent);

            await using var source = file.Entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copied = await CopyExactAsync(
                source,
                destination,
                cancellationToken);
            if (copied != file.Entry.Length)
            {
                throw Failure(
                    $"ZIP entry '{file.RelativePath}' changed while being extracted.");
            }

            await destination.FlushAsync(cancellationToken);
            extractedBytes = checked(extractedBytes + copied);
            extractedFiles++;
        }

        return new MaterializedToolPackage(
            stagedInstallRoot,
            executablePath,
            extractedFiles,
            extractedBytes);
    }

    private static IReadOnlyList<ZipEntryPlan> PreflightZip(
        ZipArchive archive,
        string stagedInstallRoot,
        ToolPackageDefinition package)
    {
        var plans = new List<ZipEntryPlan>(archive.Entries.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regularFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        var fileCount = 0;

        foreach (var entry in archive.Entries)
        {
            var rawPath = entry.FullName.Replace('\\', '/');
            var isDirectory = rawPath.EndsWith("/", StringComparison.Ordinal);
            var relativePath = NormalizeZipEntryPath(rawPath, isDirectory);
            RejectSpecialEntry(entry, isDirectory, relativePath);

            if (!paths.Add(relativePath))
            {
                throw Failure(
                    $"ZIP entries collide case-insensitively at '{relativePath}'.");
            }

            _ = ToolPathPolicy.ResolveContainedRelativePath(
                stagedInstallRoot,
                relativePath);

            if (isDirectory)
            {
                if (entry.Length != 0)
                {
                    throw Failure(
                        $"ZIP directory entry '{relativePath}' contains file data.");
                }
            }
            else
            {
                fileCount = checked(fileCount + 1);
                if (fileCount > package.Limits.MaximumFileCount)
                {
                    throw Failure(
                        "The ZIP package exceeds its maximum regular-file count.");
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > package.Limits.MaximumExpandedBytes)
                {
                    throw Failure(
                        "The ZIP package exceeds its maximum expanded size.");
                }

                regularFilePaths.Add(relativePath);
            }

            plans.Add(new ZipEntryPlan(entry, relativePath, isDirectory));
        }

        foreach (var plan in plans)
        {
            var segments = plan.RelativePath.Split('/');
            if (segments.Length <= 1)
            {
                continue;
            }

            var ancestor = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                if (regularFilePaths.Contains(ancestor))
                {
                    throw Failure(
                        $"ZIP entry '{plan.RelativePath}' descends through file '{ancestor}'.");
                }

                ancestor = string.Concat(ancestor, "/", segments[index]);
            }
        }

        return plans;
    }

    private static string NormalizeZipEntryPath(
        string rawPath,
        bool isDirectory)
    {
        if (string.IsNullOrEmpty(rawPath) ||
            rawPath.StartsWith("/", StringComparison.Ordinal) ||
            rawPath.StartsWith("\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(rawPath) ||
            (rawPath.Length >= 2 &&
             char.IsLetter(rawPath[0]) &&
             rawPath[1] == ':'))
        {
            throw Failure("A ZIP entry uses an absolute or empty path.");
        }

        var trimmed = isDirectory
            ? rawPath.TrimEnd('/')
            : rawPath;
        if (string.IsNullOrEmpty(trimmed))
        {
            throw Failure("A ZIP directory entry cannot represent the archive root.");
        }

        var segments = trimmed.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".." ||
                segment.Contains(':') || segment.Any(char.IsControl)))
        {
            throw Failure(
                $"ZIP entry '{rawPath}' contains an unsafe path segment.");
        }

        return string.Join('/', segments);
    }

    private static void RejectSpecialEntry(
        ZipArchiveEntry entry,
        bool isDirectory,
        string relativePath)
    {
        var dosAttributes =
            (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if ((dosAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                $"ZIP entry '{relativePath}' is a reparse point.");
        }

        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var unixFileType = unixMode & UnixFileTypeMask;
        if (unixFileType == 0)
        {
            return;
        }

        var expectedType = isDirectory ? UnixDirectory : UnixRegularFile;
        if (unixFileType != expectedType)
        {
            throw Failure(
                $"ZIP entry '{relativePath}' is a link or unsupported special file.");
        }
    }

    private static string NormalizeManifestPath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static async Task<long> CopyExactAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long copied = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (bytesRead == 0)
            {
                return copied;
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
            copied = checked(copied + bytesRead);
        }
    }

    private static bool IsExpectedMaterializationFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            OverflowException;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ToolOperationException Failure(
        string message,
        Exception? innerException = null) =>
        new("ToolInstallationFailed", message, innerException);

    private sealed record ZipEntryPlan(
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory);
}
