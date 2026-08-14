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

        var root = Path.GetFullPath(dataRoot);
        var candidates = EnumerateIndexRoots(root, codebase, channel, indexId);
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

    private static string[] EnumerateIndexRoots(
        string root,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        IEnumerable<string> candidates = codebase switch
        {
            CodebaseKind.ScheduleI when channel == CodeChannel.Installed =>
                EnumerateChildren(Path.Combine(root, "builds"), "indexes", indexId),
            CodebaseKind.S1Api or CodebaseKind.S1MApi when channel == CodeChannel.Installed =>
                EnumerateNested(Path.Combine(root, "installed", ToSegment(codebase)), "indexes", indexId),
            CodebaseKind.S1Api or CodebaseKind.S1MApi when channel is CodeChannel.Release or CodeChannel.Preview =>
                EnumerateNested(Path.Combine(root, "upstream", ToSegment(codebase), "commits"), "indexes", indexId),
            _ => throw new NotSupportedException("Integrity-checked source path resolution is not available for this codebase/channel.")
        };
        return candidates.Where(Directory.Exists).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateChildren(string root, string indexesSegment, string indexId)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var child in Directory.EnumerateDirectories(root))
            yield return Path.Combine(child, indexesSegment, indexId);
    }

    private static IEnumerable<string> EnumerateNested(string root, string indexesSegment, string indexId)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var child in Directory.EnumerateDirectories(root))
            yield return Path.Combine(child, indexesSegment, indexId);
    }

    private static string ToSegment(CodebaseKind codebase) => codebase switch
    {
        CodebaseKind.S1Api => "s1api",
        CodebaseKind.S1MApi => "s1mapi",
        _ => throw new ArgumentOutOfRangeException(nameof(codebase))
    };
}
