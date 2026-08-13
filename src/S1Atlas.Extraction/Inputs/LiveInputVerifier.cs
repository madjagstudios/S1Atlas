using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;

namespace S1Atlas.Extraction.Inputs;

internal sealed record ResolvedExtractionInput(
    ExtractionInputSource Source,
    string GameRoot,
    string GameAssemblyPath,
    string GlobalMetadataPath,
    string ExecutablePath,
    string UnityVersionSourcePath,
    string? InputSnapshotId);

internal sealed class LiveInputVerifier
{
    private readonly IFileHasher _fileHasher;

    public LiveInputVerifier(IFileHasher fileHasher)
    {
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
    }

    public async Task<InputManifest> CaptureAsync(
        ResolvedExtractionInput installation,
        GameBuild selectedBuild,
        ExtractionProfile profile,
        CancellationToken cancellationToken,
        ExtractionFailureStage failureStage =
            ExtractionFailureStage.PreRunInputVerification)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(selectedBuild);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var root = NormalizeRoot(installation.GameRoot, failureStage);
        var declarations = CreateDeclarations(installation, profile, root, failureStage);
        var entries = new List<InputManifestEntry>(declarations.Count);
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await CaptureFileAsync(
                root,
                declaration.RelativePath,
                declaration.Role,
                failureStage,
                cancellationToken));
        }

        var manifest = new InputManifest(
            entries
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray());
        RequireBuildHashes(manifest, selectedBuild, failureStage);
        return manifest;
    }

    public void VerifyUnchanged(
        InputManifest preRun,
        InputManifest postRun,
        GameBuild selectedBuild)
    {
        ArgumentNullException.ThrowIfNull(preRun);
        ArgumentNullException.ThrowIfNull(postRun);
        ArgumentNullException.ThrowIfNull(selectedBuild);

        RequireBuildHashes(
            preRun,
            selectedBuild,
            ExtractionFailureStage.PostRunInputVerification);
        RequireBuildHashes(
            postRun,
            selectedBuild,
            ExtractionFailureStage.PostRunInputVerification);

        var preByIdentity = preRun.Entries.ToDictionary(
            entry => (entry.RelativePath.Replace('\\', '/'), entry.Role));
        var postByIdentity = postRun.Entries.ToDictionary(
            entry => (entry.RelativePath.Replace('\\', '/'), entry.Role));
        if (preByIdentity.Count != postByIdentity.Count ||
            preByIdentity.Any(pair =>
                !postByIdentity.TryGetValue(pair.Key, out var post) ||
                !string.Equals(
                    pair.Value.Sha256,
                    post.Sha256,
                    StringComparison.Ordinal)))
        {
            throw Failure(
                ExtractionFailureStage.PostRunInputVerification,
                ExtractionFailureCode.InputChangedDuringExtraction,
                "Extraction input content changed while Cpp2IL was running.");
        }
    }

    private async Task<InputManifestEntry> CaptureFileAsync(
        string root,
        string relativePath,
        string role,
        ExtractionFailureStage failureStage,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!PathSafety.IsContained(root, path) ||
            !PathSafety.TryObserveRegularFile(root, path, out var before))
        {
            throw MissingOrUnsafe(failureStage, relativePath);
        }

        string hash;
        try
        {
            hash = await _fileHasher.ComputeSha256Async(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw MissingOrUnsafe(failureStage, relativePath, exception);
        }

        if (!PathSafety.TryObserveRegularFile(root, path, out var after))
        {
            throw MissingOrUnsafe(failureStage, relativePath);
        }

        if (!before.IsStableWith(after))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retryBefore = after;
            try
            {
                hash = await _fileHasher.ComputeSha256Async(path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw Unstable(failureStage, relativePath, exception);
            }

            if (!PathSafety.TryObserveRegularFile(root, path, out var retryAfter) ||
                !retryBefore.IsStableWith(retryAfter))
            {
                throw Unstable(failureStage, relativePath);
            }

            after = retryAfter;
        }

        return new InputManifestEntry(
            relativePath.Replace('\\', '/'),
            role,
            after.Length,
            hash,
            after.LastWriteUtc);
    }

    private static string NormalizeRoot(
        string rootPath,
        ExtractionFailureStage failureStage)
    {
        try
        {
            var root = Path.GetFullPath(rootPath);
            if (!PathSafety.IsNormalDirectory(root))
            {
                throw MissingOrUnsafe(failureStage, "game root");
            }

            return root;
        }
        catch (ExtractionOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw MissingOrUnsafe(failureStage, "game root", exception);
        }
    }

    private static IReadOnlyList<FileDeclaration> CreateDeclarations(
        ResolvedExtractionInput installation,
        ExtractionProfile profile,
        string root,
        ExtractionFailureStage failureStage)
    {
        var declarations = profile.SnapshotInputs
            .Select(input => new FileDeclaration(
                NormalizeRelativePath(input.RelativePath, root, failureStage),
                input.Role))
            .ToList();
        var expectedPaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gameAssembly"] = installation.GameAssemblyPath,
            ["globalMetadata"] = installation.GlobalMetadataPath,
            ["executableSupport"] = installation.ExecutablePath
        };
        foreach (var expected in expectedPaths)
        {
            var declaration = declarations.SingleOrDefault(item => item.Role == expected.Key);
            if (declaration is null ||
                !PathSafety.PathsEqual(
                    Path.GetFullPath(Path.Combine(root, declaration.RelativePath)),
                    Path.GetFullPath(expected.Value)))
            {
                throw Failure(
                    failureStage,
                    FailureCodeForChange(failureStage),
                    $"Extraction profile input role '{expected.Key}' does not match the resolved game input.");
            }
        }

        var unityPath = Path.GetFullPath(installation.UnityVersionSourcePath);
        if (!PathSafety.IsContained(root, unityPath))
        {
            throw MissingOrUnsafe(failureStage, "Unity version source");
        }

        var unityRelativePath = Path.GetRelativePath(root, unityPath).Replace('\\', '/');
        if (declarations.All(item =>
                !string.Equals(
                    item.RelativePath,
                    unityRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            declarations.Add(new FileDeclaration(unityRelativePath, "unityVersionSource"));
        }

        return declarations;
    }

    private static string NormalizeRelativePath(
        string relativePath,
        string root,
        ExtractionFailureStage failureStage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw MissingOrUnsafe(failureStage, "profile input");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!PathSafety.IsContained(root, fullPath))
            {
                throw MissingOrUnsafe(failureStage, "profile input");
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
            throw MissingOrUnsafe(failureStage, "profile input", exception);
        }
    }

    private static void RequireBuildHashes(
        InputManifest manifest,
        GameBuild selectedBuild,
        ExtractionFailureStage failureStage)
    {
        var assembly = manifest.Entries.SingleOrDefault(entry => entry.Role == "gameAssembly");
        var metadata = manifest.Entries.SingleOrDefault(entry => entry.Role == "globalMetadata");
        if (assembly is null || metadata is null ||
            !string.Equals(
                assembly.Sha256,
                selectedBuild.GameAssemblySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.Sha256,
                selectedBuild.MetadataSha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                failureStage,
                FailureCodeForChange(failureStage),
                failureStage == ExtractionFailureStage.PostRunInputVerification
                    ? "Extraction input content changed while Cpp2IL was running."
                    : "Game input content does not match the selected Atlas build.");
        }
    }

    private static ExtractionOperationException MissingOrUnsafe(
        ExtractionFailureStage stage,
        string description,
        Exception? innerException = null) =>
        Failure(
            stage,
            stage == ExtractionFailureStage.PostRunInputVerification
                ? ExtractionFailureCode.InputChangedDuringExtraction
                : ExtractionFailureCode.LiveInputNotFound,
            $"Required extraction input '{description}' is missing or unsafe.",
            innerException);

    private static ExtractionOperationException Unstable(
        ExtractionFailureStage stage,
        string relativePath,
        Exception? innerException = null) =>
        Failure(
            stage,
            FailureCodeForChange(stage),
            $"Extraction input '{relativePath}' did not remain stable while it was hashed.",
            innerException);

    private static ExtractionFailureCode FailureCodeForChange(
        ExtractionFailureStage stage) =>
        stage == ExtractionFailureStage.PostRunInputVerification
            ? ExtractionFailureCode.InputChangedDuringExtraction
            : ExtractionFailureCode.BuildInputMismatch;

    private static ExtractionOperationException Failure(
        ExtractionFailureStage stage,
        ExtractionFailureCode code,
        string message,
        Exception? innerException = null) =>
        new(stage, code, message, innerException: innerException);

    private sealed record FileDeclaration(string RelativePath, string Role);
}

internal static class PathSafety
{
    public static bool IsNormalDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists &&
                (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                !HasReparsePointInExistingAncestry(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryObserveRegularFile(
        string root,
        string path,
        out FileObservation observation)
    {
        observation = default;
        try
        {
            if (!File.Exists(path) || !IsContained(root, path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                HasReparsePointBetween(root, Path.GetDirectoryName(path)!))
            {
                return false;
            }

            var file = new FileInfo(path);
            observation = new FileObservation(
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                attributes);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePointInExistingAncestry(DirectoryInfo? directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
        {
            current.Refresh();
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePointBetween(string root, string directoryPath)
    {
        var relative = Path.GetRelativePath(root, directoryPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal readonly record struct FileObservation(
    long Length,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes)
{
    public bool IsStableWith(FileObservation other) =>
        Length == other.Length &&
        LastWriteUtc == other.LastWriteUtc &&
        Attributes == other.Attributes;
}
