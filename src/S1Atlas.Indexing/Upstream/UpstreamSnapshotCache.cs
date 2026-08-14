using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Paths;

namespace S1Atlas.Indexing.Upstream;

public sealed class UpstreamSnapshotCache
{
    private readonly string _dataRoot;

    public UpstreamSnapshotCache(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
    }

    public async Task SaveAsync(UpstreamSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var paths = OwnedIndexPaths.ForUpstream(_dataRoot, ToSegment(snapshot.Repository.Codebase), snapshot.CommitSha);
        Directory.CreateDirectory(paths.StagingRoot);
        try
        {
            foreach (var file in snapshot.Files)
            {
                var fullPath = ResolveContainedFile(paths.StagingRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllBytesAsync(fullPath, file.Content, cancellationToken);
                var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath, cancellationToken))).ToLowerInvariant();
                if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("The upstream file hash did not validate.");
            }
            var manifest = JsonSerializer.Serialize(snapshot.Files.Select(file => new { file.RelativePath, file.Sha256 }).ToArray());
            await File.WriteAllTextAsync(Path.Combine(paths.StagingRoot, "snapshot-manifest.json"), manifest, cancellationToken);
            if (Directory.Exists(paths.FinalRoot)) Directory.Delete(paths.FinalRoot, recursive: true);
            Directory.Move(paths.StagingRoot, paths.FinalRoot);
        }
        catch
        {
            if (Directory.Exists(paths.StagingRoot)) Directory.Delete(paths.StagingRoot, recursive: true);
            throw;
        }
    }

    public bool Exists(CodebaseKind codebase, string commitSha) =>
        Directory.Exists(OwnedIndexPaths.ForUpstream(_dataRoot, ToSegment(codebase), commitSha).FinalRoot);

    public async Task<IReadOnlyList<UpstreamFile>> ReadFilesAsync(
        CodebaseKind codebase,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var paths = OwnedIndexPaths.ForUpstream(_dataRoot, ToSegment(codebase), commitSha);
        if (!Directory.Exists(paths.FinalRoot))
            throw new FileNotFoundException("The requested upstream commit is not cached.", paths.FinalRoot);

        var manifestPath = Path.Combine(paths.FinalRoot, "snapshot-manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("The cached upstream commit has no complete manifest.");

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestEntry[]>(
            manifestStream,
            cancellationToken: cancellationToken) ?? throw new InvalidDataException("The cached upstream manifest is empty.");
        var files = new List<UpstreamFile>(manifest.Length);
        foreach (var entry in manifest.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var fullPath = ResolveContainedFile(paths.FinalRoot, entry.RelativePath);
            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The cached upstream file hash did not validate for '{entry.RelativePath}'.");
            files.Add(new UpstreamFile(entry.RelativePath, content, actualSha256));
        }
        return files;
    }

    public IReadOnlyList<string> GetCachedCommits(CodebaseKind codebase)
    {
        var root = Path.Combine(_dataRoot, "upstream", ToSegment(codebase), "commits");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: 40 } && name.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            .Where(name => File.Exists(Path.Combine(root, name!, "snapshot-manifest.json")))
            .Select(name => name!)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveContainedFile(string stagingRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("The upstream file path escaped the cache root.");
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("The upstream file path escaped the cache root.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The upstream file path escaped the cache root.");
        return fullPath;
    }

    private static string ToSegment(CodebaseKind codebase) => codebase switch
    {
        CodebaseKind.S1Api => "s1api",
        CodebaseKind.S1MApi => "s1mapi",
        _ => throw new ArgumentException("Only API codebases have upstream snapshots.", nameof(codebase))
    };

    private sealed record ManifestEntry(string RelativePath, string Sha256);
}
