using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

public sealed class VerifiedSourceFileResolver
{
    private readonly string _dataRoot;
    private readonly SourceSnippetReader _reader = new();

    public VerifiedSourceFileResolver(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public async Task<VerifiedSourceFileQueryResult> ReadAsync(
        SourceSnippetQueryResult source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!Enum.TryParse<CodebaseKind>(source.Symbol.Codebase, ignoreCase: true, out var codebase) ||
            !Enum.TryParse<CodeChannel>(source.Symbol.Channel, ignoreCase: true, out var channel))
            throw new InvalidDataException("The indexed source provenance has an unrecognized codebase or channel.");

        var indexRoot = ResolveIndexRoot(_dataRoot, codebase, channel, source.IndexId);
        var sourcePath = ResolveContainedSourcePath(indexRoot, source.RelativePath);
        var bytes = await _reader.ReadVerifiedBytesAsync(
            sourcePath,
            source.Sha256,
            cancellationToken);

        if (bytes.LongLength != source.ByteCount)
            throw new InvalidDataException("The indexed source file byte count does not match the recorded source metadata.");

        return new VerifiedSourceFileQueryResult(source, bytes);
    }

    private static string ResolveIndexRoot(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        if (codebase != CodebaseKind.ScheduleI || channel != CodeChannel.Installed)
            throw new NotSupportedException("Integrity-checked source path resolution is not yet available for this codebase/channel.");

        var buildsRoot = Path.Combine(dataRoot, "builds");
        if (!Directory.Exists(buildsRoot))
            throw new FileNotFoundException("The Atlas build index root was not found.", buildsRoot);

        var candidates = new List<string>();
        foreach (var buildRoot in Directory.EnumerateDirectories(buildsRoot))
        {
            if ((File.GetAttributes(buildRoot) & FileAttributes.ReparsePoint) != 0)
                continue;

            var candidate = Path.Combine(buildRoot, "indexes", indexId);
            if (Directory.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
                candidates.Add(Path.GetFullPath(candidate));
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("The completed Atlas index source root was not found."),
            _ => throw new InvalidDataException("Multiple Atlas-owned source roots matched the completed index identity.")
        };
    }

    private static string ResolveContainedSourcePath(string indexRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The indexed source path is not a safe relative path.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("The indexed source path escaped its Atlas-owned index root.");

        return fullPath;
    }
}
