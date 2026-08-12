using S1Atlas.Extraction.Discovery;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class SafeDependencyFileEnumeratorTests
{
    [Fact]
    public void EnumerateDlls_WhenChildCannotBeRead_SkipsItAndKeepsAccessibleFiles()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "virtual-root"));
        var blocked = Path.Combine(root, "blocked");
        var accessible = Path.Combine(root, "accessible");
        var rootFile = Path.Combine(root, "S1API.dll");
        var nestedFile = Path.Combine(accessible, "S1MAPI.dll");
        var enumerator = new SafeDependencyFileEnumerator(
            _ => true,
            directory => directory switch
            {
                var value when SamePath(value, root) => [rootFile],
                var value when SamePath(value, blocked) =>
                    throw new UnauthorizedAccessException("simulated inaccessible directory"),
                var value when SamePath(value, accessible) => [nestedFile],
                _ => []
            },
            directory => SamePath(directory, root) ? [blocked, accessible] : [],
            _ => FileAttributes.Directory);

        var result = enumerator.EnumerateDlls(root);

        Assert.Equal(
            new[] { rootFile, nestedFile }
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal),
            result);
    }

    [Fact]
    public void EnumerateDlls_WhenChildIsReparsePoint_DoesNotTraverseIt()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "virtual-root"));
        var loop = Path.Combine(root, "loop");
        var loopWasEnumerated = false;
        var enumerator = new SafeDependencyFileEnumerator(
            _ => true,
            directory =>
            {
                if (SamePath(directory, loop))
                {
                    loopWasEnumerated = true;
                }

                return [];
            },
            directory => SamePath(directory, root) ? [loop] : [],
            directory => SamePath(directory, loop)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory);

        var result = enumerator.EnumerateDlls(root);

        Assert.Empty(result);
        Assert.False(loopWasEnumerated);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
