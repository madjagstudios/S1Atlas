using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;

namespace S1Atlas.Extraction.Inputs;

internal sealed class InputSnapshotDocumentStore
{
    private const string ManifestFileName = "input-manifest.json";
    private const string MarkerFileName = "complete.marker";
    private const string GameRootDirectoryName = "game-root";

    private static readonly UTF8Encoding Utf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task WriteAsync(
        string stagingRoot,
        InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(stagingRoot);
        var manifest = new InputManifestDocument
        {
            SchemaVersion = 1,
            BuildId = snapshot.BuildId,
            ManifestDigest = snapshot.ManifestDigest,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            Files = snapshot.Manifest.Entries
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => new InputManifestFileDocument
                {
                    RelativePath = file.RelativePath,
                    Role = file.Role,
                    Size = file.Size,
                    Sha256 = file.Sha256,
                    LastWriteUtc = file.LastWriteUtc
                })
                .Select(file => (InputManifestFileDocument?)file)
                .ToList()
        };
        var marker = new InputSnapshotMarkerDocument
        {
            MarkerSchemaVersion = 1,
            InputSnapshotId = snapshot.InputSnapshotId,
            ManifestDigest = snapshot.ManifestDigest
        };

        await WriteNewAsync(
            Path.Combine(root, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteNewAsync(
            Path.Combine(root, MarkerFileName),
            JsonSerializer.Serialize(marker, JsonOptions),
            cancellationToken);
    }

    public async Task<InputSnapshot?> TryReadVerifiedAsync(
        string snapshotRoot,
        string expectedBuildId,
        string expectedSnapshotId,
        IFileHasher fileHasher,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshotId);
        ArgumentNullException.ThrowIfNull(fileHasher);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(snapshotRoot);
            if (!PathSafety.IsNormalDirectory(root))
            {
                return null;
            }

            var manifestPath = Path.Combine(root, ManifestFileName);
            var markerPath = Path.Combine(root, MarkerFileName);
            if (!PathSafety.TryObserveRegularFile(root, manifestPath, out _) ||
                !PathSafety.TryObserveRegularFile(root, markerPath, out _))
            {
                return null;
            }

            var manifestDocument = JsonSerializer.Deserialize<InputManifestDocument>(
                await File.ReadAllTextAsync(
                    manifestPath,
                    Utf8NoBom,
                    cancellationToken),
                JsonOptions);
            var markerDocument = JsonSerializer.Deserialize<InputSnapshotMarkerDocument>(
                await File.ReadAllTextAsync(
                    markerPath,
                    Utf8NoBom,
                    cancellationToken),
                JsonOptions);
            if (!TryMaterialize(
                    root,
                    expectedBuildId,
                    expectedSnapshotId,
                    manifestDocument,
                    markerDocument,
                    out var snapshot))
            {
                return null;
            }

            var expectedEntries = CreateExpectedEntries(snapshot.Manifest);
            if (!TryCollectEntries(root, out var actualEntries) ||
                !expectedEntries.SetEquals(actualEntries))
            {
                return null;
            }

            var gameRoot = Path.Combine(root, GameRootDirectoryName);
            foreach (var file in snapshot.Manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveFilePath(gameRoot, file.RelativePath);
                if (path is null ||
                    !PathSafety.TryObserveRegularFile(gameRoot, path, out var observation) ||
                    observation.Length != file.Size)
                {
                    return null;
                }

                var hash = await fileHasher.ComputeSha256Async(path, cancellationToken);
                if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryMaterialize(
        string root,
        string expectedBuildId,
        string expectedSnapshotId,
        InputManifestDocument? manifestDocument,
        InputSnapshotMarkerDocument? markerDocument,
        [NotNullWhen(true)] out InputSnapshot? snapshot)
    {
        snapshot = null;
        if (manifestDocument?.SchemaVersion != 1 ||
            !string.Equals(manifestDocument.BuildId, expectedBuildId, StringComparison.Ordinal) ||
            !IsLowerSha256(manifestDocument.ManifestDigest) ||
            manifestDocument.CreatedAtUtc is null ||
            manifestDocument.Files is null ||
            markerDocument?.MarkerSchemaVersion != 1 ||
            !string.Equals(
                markerDocument.InputSnapshotId,
                expectedSnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(
                markerDocument.ManifestDigest,
                manifestDocument.ManifestDigest,
                StringComparison.Ordinal))
        {
            return false;
        }

        var files = new List<InputManifestEntry>(manifestDocument.Files.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in manifestDocument.Files)
        {
            if (document is null ||
                !TryNormalizeRelativePath(document.RelativePath, out var relativePath) ||
                !paths.Add(relativePath) ||
                string.IsNullOrWhiteSpace(document.Role) ||
                document.Size is null or < 0 ||
                !IsLowerSha256(document.Sha256) ||
                document.LastWriteUtc is null)
            {
                return false;
            }

            files.Add(new InputManifestEntry(
                relativePath,
                document.Role,
                document.Size.Value,
                document.Sha256,
                document.LastWriteUtc.Value));
        }

        files.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        var manifest = new InputManifest(files);
        var digest = InputManifestFingerprint.Create(manifest);
        var snapshotId = InputManifestFingerprint.CreateSnapshotId(expectedBuildId, digest);
        if (!string.Equals(digest, manifestDocument.ManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(snapshotId, expectedSnapshotId, StringComparison.Ordinal))
        {
            return false;
        }

        snapshot = new InputSnapshot(
            snapshotId,
            expectedBuildId,
            root,
            digest,
            manifestDocument.CreatedAtUtc.Value.ToUniversalTime(),
            ReplayVerified: false,
            ReplayVerifiedAtUtc: null,
            manifest);
        return true;
    }

    private static HashSet<string> CreateExpectedEntries(InputManifest manifest)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ManifestFileName,
            MarkerFileName,
            GameRootDirectoryName
        };
        foreach (var file in manifest.Entries)
        {
            var path = $"{GameRootDirectoryName}/{file.RelativePath}";
            expected.Add(path);
            var parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                expected.Add(parent.Replace('\\', '/'));
                parent = Path.GetDirectoryName(parent);
            }
        }

        return expected;
    }

    private static bool TryCollectEntries(string root, out HashSet<string> entries)
    {
        entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!entries.Add(relative))
                {
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }

        return true;
    }

    private static string? ResolveFilePath(string root, string relativePath)
    {
        if (!TryNormalizeRelativePath(relativePath, out var normalized))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        return PathSafety.IsContained(root, path) ? path : null;
    }

    private static bool TryNormalizeRelativePath(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':') ||
            value.Any(char.IsControl))
        {
            return false;
        }

        var candidate = value.Replace('\\', '/');
        var segments = candidate.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static bool IsLowerSha256([NotNullWhen(true)] string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteNewAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var bytes = Utf8NoBom.GetBytes(contents);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed class InputManifestDocument
    {
        public int? SchemaVersion { get; init; }
        public string? BuildId { get; init; }
        public string? ManifestDigest { get; init; }
        public DateTimeOffset? CreatedAtUtc { get; init; }
        public List<InputManifestFileDocument?>? Files { get; init; }
    }

    private sealed class InputManifestFileDocument
    {
        public string? RelativePath { get; init; }
        public string? Role { get; init; }
        public long? Size { get; init; }
        public string? Sha256 { get; init; }
        public DateTimeOffset? LastWriteUtc { get; init; }
    }

    private sealed class InputSnapshotMarkerDocument
    {
        public int? MarkerSchemaVersion { get; init; }
        public string? InputSnapshotId { get; init; }
        public string? ManifestDigest { get; init; }
    }
}
