using S1Atlas.Indexing.Paths;
using Xunit;

namespace S1Atlas.Indexing.Tests.Paths;

public sealed class OwnedIndexPathsTests
{
    private const string DataRoot = "C:\\atlas";
    private static readonly string BuildId = new('a', 64);
    private static readonly string BinarySha = new('b', 64);
    private static readonly string IndexId = new('c', 64);
    private static readonly string CommitSha = new('d', 40);

    [Fact]
    public void ScheduleOneIndex_UsesBuildIndexRootAndCompletionMarker()
    {
        var paths = OwnedIndexPaths.ForScheduleOne(DataRoot, BuildId, IndexId);

        Assert.Equal($"C:\\atlas\\builds\\{BuildId}\\indexes\\{IndexId}", paths.FinalRoot);
        Assert.Equal(paths.FinalRoot + ".staging", paths.StagingRoot);
        Assert.Equal(Path.Combine(paths.FinalRoot, "complete.marker"), paths.CompleteMarkerPath);
    }

    [Theory]
    [InlineData("s1api")]
    [InlineData("s1mapi")]
    public void InstalledIndex_UsesCodebaseAndBinaryHashRoot(string codebase)
    {
        var paths = OwnedIndexPaths.ForInstalled(DataRoot, codebase, BinarySha, IndexId);

        Assert.Equal(
            $"C:\\atlas\\installed\\{codebase}\\{BinarySha}\\indexes\\{IndexId}",
            paths.FinalRoot);
        Assert.Equal(paths.FinalRoot + ".staging", paths.StagingRoot);
    }

    [Theory]
    [InlineData("s1api")]
    [InlineData("s1mapi")]
    public void UpstreamSnapshot_UsesRepositoryCommitRoot(string codebase)
    {
        var paths = OwnedIndexPaths.ForUpstream(DataRoot, codebase, CommitSha);

        Assert.Equal(
            $"C:\\atlas\\upstream\\{codebase}\\commits\\{CommitSha}",
            paths.FinalRoot);
        Assert.Null(paths.CompleteMarkerPath);
    }

    [Theory]
    [InlineData("..\\escape")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void UnsafeSegment_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            OwnedIndexPaths.ForScheduleOne(DataRoot, value, IndexId));
    }

    [Fact]
    public void InvalidCodebase_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            OwnedIndexPaths.ForInstalled(DataRoot, "schedule-i", BinarySha, IndexId));
    }

    [Fact]
    public void ExistingReparsePointAncestor_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OwnedIndexPaths.ForScheduleOne(
                DataRoot,
                BuildId,
                IndexId,
                path => path.EndsWith("\\builds", StringComparison.Ordinal)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : throw new FileNotFoundException(path)));
    }
}
