using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Inputs;

namespace S1Atlas.Extraction.Scene;

public sealed class SceneInputVerifier
{
    private static readonly HashSet<string> SupportedContainerPaths = new(
        new[]
        {
            "Schedule I_Data/level0",
            "Schedule I_Data/level1",
            "Schedule I_Data/level2",
            "Schedule I_Data/sharedassets0.assets",
            "Schedule I_Data/sharedassets1.assets",
            "Schedule I_Data/sharedassets2.assets",
            "Schedule I_Data/resources.assets",
            "Schedule I_Data/globalgamemanagers",
            "Schedule I_Data/globalgamemanagers.assets"
        },
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IFileHasher _fileHasher;

    public SceneInputVerifier(IFileHasher fileHasher)
    {
        _fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
    }

    public async Task<VerifiedSceneInput> CaptureAsync(
        string installRoot,
        IReadOnlyList<SceneContainerDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(declarations);
        cancellationToken.ThrowIfCancellationRequested();

        var root = NormalizeRoot(installRoot);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = new List<VerifiedSceneContainer>(declarations.Count);
        var verifiedFiles = new List<VerifiedSceneFile>();

        foreach (var declaration in declarations)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(declaration.SidecarRelativePaths);
            cancellationToken.ThrowIfCancellationRequested();

            var primaryRelativePath = ResolveRelativePath(
                root,
                declaration.RelativePath,
                paths);
            RequireSupportedContainer(primaryRelativePath);
            var primaryPath = Path.GetFullPath(Path.Combine(root, primaryRelativePath));
            var primary = await CaptureFileAsync(
                root,
                primaryRelativePath,
                primaryPath,
                cancellationToken);
            var header = ReadSerializedFileHeader(primaryPath);
            RequireStillObserved(root, primary);

            var sidecars = new List<VerifiedSceneFile>(declaration.SidecarRelativePaths.Count);
            foreach (var sidecarRelativePath in declaration.SidecarRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonicalRelativePath = ResolveRelativePath(
                    root,
                    sidecarRelativePath,
                    paths);
                RequireMatchingSidecar(primaryRelativePath, canonicalRelativePath);
                var sidecarPath = Path.GetFullPath(Path.Combine(root, canonicalRelativePath));
                sidecars.Add(await CaptureFileAsync(
                    root,
                    canonicalRelativePath,
                    sidecarPath,
                    cancellationToken));
            }

            sidecars.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            var sidecarManifest = SerializeSidecars(sidecars);
            containers.Add(new VerifiedSceneContainer(
                primary.RelativePath,
                primary.FullPath,
                sidecars.Select(sidecar => sidecar.FullPath).ToArray(),
                primary.Sha256,
                primary.ByteCount,
                header.UnityVersion,
                header.SerializedFileVersion,
                sidecarManifest));
            verifiedFiles.Add(primary);
            verifiedFiles.AddRange(sidecars);
        }

        containers.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        verifiedFiles.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        var immutableContainers = containers.ToArray();
        return new VerifiedSceneInput(
            root,
            immutableContainers,
            ComputeManifestDigest(immutableContainers),
            verifiedFiles.ToArray());
    }

    public async Task VerifyAfterParsingAsync(
        VerifiedSceneInput verifiedInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiedInput);
        cancellationToken.ThrowIfCancellationRequested();

        var root = NormalizeRoot(verifiedInput.InstallRoot);
        foreach (var expected in verifiedInput.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathSafety.IsContained(root, expected.FullPath) ||
                !PathSafety.TryObserveRegularFile(root, expected.FullPath, out var before))
            {
                throw UnsafeInput(expected.RelativePath);
            }

            string hash;
            try
            {
                hash = await _fileHasher.ComputeSha256Async(
                    expected.FullPath,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw UnsafeInput(expected.RelativePath, exception);
            }

            if (!PathSafety.TryObserveRegularFile(root, expected.FullPath, out var after) ||
                !before.IsStableWith(after) ||
                before.Length != expected.ByteCount ||
                before.LastWriteUtc != expected.LastWriteUtc ||
                !string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
            {
                throw ChangedInput(expected.RelativePath);
            }
        }
    }

    private async Task<VerifiedSceneFile> CaptureFileAsync(
        string root,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (!PathSafety.TryObserveRegularFile(root, fullPath, out var before))
        {
            throw UnsafeInput(relativePath);
        }

        string hash;
        try
        {
            hash = await _fileHasher.ComputeSha256Async(fullPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw UnsafeInput(relativePath, exception);
        }

        if (!PathSafety.TryObserveRegularFile(root, fullPath, out var after))
        {
            throw UnsafeInput(relativePath);
        }

        if (!before.IsStableWith(after))
        {
            cancellationToken.ThrowIfCancellationRequested();
            before = after;
            try
            {
                hash = await _fileHasher.ComputeSha256Async(fullPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw ChangedInput(relativePath, exception);
            }

            if (!PathSafety.TryObserveRegularFile(root, fullPath, out after) ||
                !before.IsStableWith(after))
            {
                throw ChangedInput(relativePath);
            }
        }

        return new VerifiedSceneFile(
            relativePath,
            fullPath,
            after.Length,
            after.LastWriteUtc,
            hash);
    }

    private static string NormalizeRoot(string installRoot)
    {
        try
        {
            var root = Path.GetFullPath(installRoot);
            if (!PathSafety.IsNormalDirectory(root))
            {
                throw UnsafeInput("install root");
            }

            return root;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw UnsafeInput("install root", exception);
        }
    }

    private static string ResolveRelativePath(
        string root,
        string relativePath,
        HashSet<string> observedPaths)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw UnsafeInput(relativePath ?? "scene input");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!PathSafety.IsContained(root, fullPath))
            {
                throw UnsafeInput(relativePath);
            }

            var canonical = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (!observedPaths.Add(canonical))
            {
                throw new IOException($"Scene input '{canonical}' was declared more than once.");
            }

            return canonical;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw UnsafeInput(relativePath ?? "scene input", exception);
        }
    }

    private static SerializedFileHeader ReadSerializedFileHeader(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            Span<byte> originalHeader = stackalloc byte[20];
            stream.ReadExactly(originalHeader);
            var version = BinaryPrimitives.ReadUInt32BigEndian(originalHeader[8..12]);
            long declaredFileSize = BinaryPrimitives.ReadUInt32BigEndian(originalHeader[4..8]);
            if (version >= 22)
            {
                Span<byte> extendedHeader = stackalloc byte[28];
                stream.ReadExactly(extendedHeader);
                declaredFileSize = BinaryPrimitives.ReadInt64BigEndian(extendedHeader[4..12]);
            }

            if (version is < 9 or > int.MaxValue ||
                declaredFileSize != stream.Length)
            {
                throw new InvalidDataException("The scene input has an unsupported SerializedFile header.");
            }

            var versionBytes = new List<byte>(32);
            while (versionBytes.Count <= 255)
            {
                var value = stream.ReadByte();
                if (value == 0)
                {
                    break;
                }

                if (value is < 0x20 or > 0x7e)
                {
                    throw new InvalidDataException("The SerializedFile Unity version is invalid.");
                }

                versionBytes.Add((byte)value);
            }

            if (versionBytes.Count is 0 or > 255)
            {
                throw new InvalidDataException("The SerializedFile Unity version is missing or too long.");
            }

            return new SerializedFileHeader(
                Encoding.ASCII.GetString(versionBytes.ToArray()),
                checked((int)version));
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or OverflowException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The scene input is not a supported SerializedFile.", exception);
        }
    }

    private static void RequireSupportedContainer(string relativePath)
    {
        if (!SupportedContainerPaths.Contains(relativePath))
        {
            throw new IOException(
                $"Scene input '{relativePath}' is not a supported Schedule I container.");
        }
    }

    private static void RequireMatchingSidecar(string primaryPath, string sidecarPath)
    {
        var matchesResS = string.Equals(
            sidecarPath,
            $"{primaryPath}.resS",
            StringComparison.Ordinal);
        var matchesResource = primaryPath.EndsWith(".assets", StringComparison.Ordinal) &&
            string.Equals(
                sidecarPath,
                $"{primaryPath[..^".assets".Length]}.resource",
                StringComparison.Ordinal);
        if (!matchesResS && !matchesResource)
        {
            throw new IOException(
                $"Scene sidecar '{sidecarPath}' does not match container '{primaryPath}'.");
        }
    }

    private static void RequireStillObserved(string root, VerifiedSceneFile file)
    {
        if (!PathSafety.TryObserveRegularFile(root, file.FullPath, out var observation) ||
            observation.Length != file.ByteCount ||
            observation.LastWriteUtc != file.LastWriteUtc)
        {
            throw ChangedInput(file.RelativePath);
        }
    }

    private static string SerializeSidecars(IReadOnlyList<VerifiedSceneFile> sidecars) =>
        JsonSerializer.Serialize(sidecars.Select(sidecar => new SidecarManifestEntry(
            sidecar.RelativePath,
            sidecar.ByteCount,
            sidecar.Sha256)), ManifestJsonOptions);

    private static string ComputeManifestDigest(
        IReadOnlyList<VerifiedSceneContainer> containers)
    {
        var manifest = JsonSerializer.Serialize(containers.Select(container =>
            new ContainerManifestEntry(
                container.RelativePath,
                container.ByteCount,
                container.Sha256,
                container.UnityVersion,
                container.SerializedFileVersion,
                container.SidecarManifest)), ManifestJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))
            .ToLowerInvariant();
    }

    private static IOException UnsafeInput(
        string relativePath,
        Exception? innerException = null) =>
        new($"Scene input '{relativePath}' is missing or unsafe.", innerException);

    private static IOException ChangedInput(
        string relativePath,
        Exception? innerException = null) =>
        new($"Scene input '{relativePath}' changed during parsing.", innerException);

    private sealed record SerializedFileHeader(
        string UnityVersion,
        int SerializedFileVersion);

    private sealed record SidecarManifestEntry(
        string RelativePath,
        long ByteCount,
        string Sha256);

    private sealed record ContainerManifestEntry(
        string RelativePath,
        long ByteCount,
        string Sha256,
        string UnityVersion,
        int SerializedFileVersion,
        string SidecarManifest);
}
