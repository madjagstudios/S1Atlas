using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

public sealed class VerifiedIndexedSourceReader
{
    private readonly SourceSnippetReader _reader = new();

    public async Task<byte[]> ReadAsync(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId,
        string relativePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (codebase != CodebaseKind.ScheduleI || channel != CodeChannel.Installed)
            throw new NotSupportedException("Integrity-checked source path resolution is not yet available for this codebase/channel.");

        var root = Path.GetFullPath(dataRoot);
        var buildsRoot = Path.Combine(root, "builds");
        if (!Directory.Exists(buildsRoot))
            throw new FileNotFoundException("The Atlas build index root was not found.", buildsRoot);

        var candidates = Directory.EnumerateDirectories(buildsRoot)
            .Select(buildRoot => Path.Combine(buildRoot, "indexes", indexId))
            .Where(Directory.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            throw new FileNotFoundException("The completed Atlas index directory was not found.", indexId);
        if (candidates.Length > 1)
            throw new InvalidDataException("The completed Atlas index identity resolves to multiple directories.");

        var indexRoot = Path.GetFullPath(candidates[0]);
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
            throw new InvalidDataException("The indexed source path must be relative.");

        var sourcePath = Path.GetFullPath(Path.Combine(indexRoot, normalizedRelativePath));
        var indexPrefix = Path.TrimEndingDirectorySeparator(indexRoot) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!sourcePath.StartsWith(indexPrefix, comparison))
            throw new InvalidDataException("The indexed source path escapes its Atlas-owned index root.");

        return await _reader.ReadVerifiedBytesAsync(sourcePath, expectedSha256, cancellationToken);
    }
}
