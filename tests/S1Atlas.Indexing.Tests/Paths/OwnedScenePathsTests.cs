using S1Atlas.Indexing.Paths;
using Xunit;

namespace S1Atlas.Indexing.Tests.Paths;

public sealed class OwnedScenePathsTests
{
    private const string DataRoot = "C:\\atlas";
    private static readonly string BuildId = new('a', 64);
    private static readonly string SceneSnapshotId = new('b', 64);

    [Fact]
    public void ScheduleOneSceneIndex_UsesContainedBuildRootStagingAndCompletionMarker()
    {
        var paths = OwnedScenePaths.ForScheduleOne(DataRoot, BuildId, SceneSnapshotId);

        Assert.Equal($"C:\\atlas\\builds\\{BuildId}\\scene-indexes\\{SceneSnapshotId}", paths.FinalRoot);
        Assert.Equal(paths.FinalRoot + ".staging", paths.StagingRoot);
        Assert.Equal(Path.Combine(paths.FinalRoot, "complete.marker"), paths.CompleteMarkerPath);
        Assert.StartsWith(DataRoot + Path.DirectorySeparatorChar, paths.FinalRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(DataRoot + Path.DirectorySeparatorChar, paths.StagingRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\escape")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD")]
    public void InvalidBuildOrSnapshotSegment_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => OwnedScenePaths.ForScheduleOne(DataRoot, value, SceneSnapshotId));
        Assert.Throws<ArgumentException>(() => OwnedScenePaths.ForScheduleOne(DataRoot, BuildId, value));
    }

    [Fact]
    public void ExistingReparsePointInFinalOrStagingPath_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OwnedScenePaths.ForScheduleOne(
                DataRoot,
                BuildId,
                SceneSnapshotId,
                path => path.EndsWith("\\scene-indexes", StringComparison.Ordinal)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : throw new FileNotFoundException(path)));
    }
}
