using S1Atlas.Extraction.Cleanup;
using Xunit;

namespace S1Atlas.Extraction.Tests.Cleanup;

public sealed class CleanupTreeInspectorTests
{
    private static readonly DateTimeOffset BaseWrite =
        new(2026, 8, 13, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Inspect_NormalNestedTree_ReturnsDeterministicCountsBytesNewestAndDigest()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\a.txt", 3, BaseWrite.AddMinutes(1));
        fileSystem.AddDirectory("C:\\root\\sub", BaseWrite.AddMinutes(2));
        fileSystem.AddFile("C:\\root\\sub\\b.txt", 5, BaseWrite.AddMinutes(3));
        var inspector = new CleanupTreeInspector(fileSystem);

        var first = inspector.Inspect("C:\\root", allowMissing: false);
        var second = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Observed, first.Outcome);
        Assert.Equal(2, first.Observation!.FileCount);
        Assert.Equal(8, first.Observation.ByteCount);
        Assert.Equal(BaseWrite.AddMinutes(3), first.Observation.NewestWriteUtc);
        Assert.Matches("^[0-9a-f]{64}$", first.Observation.ObservationDigest);
        Assert.Equal(first.Observation.ObservationDigest, second.Observation!.ObservationDigest);
    }

    [Fact]
    public void Inspect_EmptyDirectory_UsesRootLastWriteAndStableDigest()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Observed, inspection.Outcome);
        Assert.Equal(0, inspection.Observation!.FileCount);
        Assert.Equal(0, inspection.Observation.ByteCount);
        Assert.Equal(BaseWrite, inspection.Observation.NewestWriteUtc);
    }

    [Fact]
    public void Inspect_OwnedRegularFileRoot_IsInspectedWithoutTraversal()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile("C:\\root\\quarantined.file", 42, BaseWrite);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root\\quarantined.file", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Observed, inspection.Outcome);
        Assert.Equal(1, inspection.Observation!.FileCount);
        Assert.Equal(42, inspection.Observation.ByteCount);
        Assert.Equal(BaseWrite, inspection.Observation.NewestWriteUtc);
    }

    [Fact]
    public void Inspect_EnumerationOrder_DoesNotChangeDigest()
    {
        var forward = BuildStandardTree(reverseEnumeration: false);
        var reversed = BuildStandardTree(reverseEnumeration: true);

        var first = new CleanupTreeInspector(forward).Inspect("C:\\root", allowMissing: false);
        var second = new CleanupTreeInspector(reversed).Inspect("C:\\root", allowMissing: false);

        Assert.Equal(
            first.Observation!.ObservationDigest,
            second.Observation!.ObservationDigest);
    }

    [Fact]
    public void Inspect_ChangingSizeLastWriteOrRelativePath_ChangesDigest()
    {
        var baseline = InspectStandardTree(size: 3, write: BaseWrite.AddMinutes(1), fileName: "a.txt");
        var differentSize = InspectStandardTree(size: 4, write: BaseWrite.AddMinutes(1), fileName: "a.txt");
        var differentWrite = InspectStandardTree(size: 3, write: BaseWrite.AddMinutes(9), fileName: "a.txt");
        var differentPath = InspectStandardTree(size: 3, write: BaseWrite.AddMinutes(1), fileName: "z.txt");

        Assert.NotEqual(baseline, differentSize);
        Assert.NotEqual(baseline, differentWrite);
        Assert.NotEqual(baseline, differentPath);
    }

    [Fact]
    public void Inspect_CaseInsensitiveDuplicatePaths_BlockInspection()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\Foo.txt", 1, BaseWrite);
        fileSystem.AddFileAllowingCollision("C:\\root\\foo.txt", 1, BaseWrite);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupCaseCollision", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_DirectoryReparsePoint_BlocksWithoutFollowing()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddDirectory("C:\\root\\link", BaseWrite, reparsePoint: true);
        // A child under the reparse point must never be visited.
        fileSystem.AddFile("C:\\root\\link\\escaped.txt", 999, BaseWrite);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupReparsePoint", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_FileReparsePoint_Blocks()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\hardlink.txt", 1, BaseWrite, reparsePoint: true);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupReparsePoint", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_ReparsePointRoot_Blocks()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite, reparsePoint: true);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupReparsePoint", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_UnreadableEntry_BlocksInspection()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\locked.txt", 1, BaseWrite, unreadable: true);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupUnreadableEntry", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_ByteCountOverflow_ReturnsBlockedResult()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\huge-a.bin", long.MaxValue, BaseWrite);
        fileSystem.AddFile("C:\\root\\huge-b.bin", 1, BaseWrite);
        var inspector = new CleanupTreeInspector(fileSystem);

        var inspection = inspector.Inspect("C:\\root", allowMissing: false);

        Assert.Equal(CleanupObservationOutcome.Blocked, inspection.Outcome);
        Assert.Equal("CleanupObservationOverflow", inspection.BlockCode);
    }

    [Fact]
    public void Inspect_MissingRoot_ReturnsEmptyObservationOnlyWhenAllowed()
    {
        var inspector = new CleanupTreeInspector(new FakeFileSystem());

        var disallowed = inspector.Inspect("C:\\missing", allowMissing: false);
        var allowed = inspector.Inspect("C:\\missing", allowMissing: true);

        Assert.Equal(CleanupObservationOutcome.Missing, disallowed.Outcome);
        Assert.Equal(CleanupObservationOutcome.Observed, allowed.Outcome);
        Assert.Equal(0, allowed.Observation!.FileCount);
        Assert.Equal(0, allowed.Observation.ByteCount);
    }

    private static FakeFileSystem BuildStandardTree(bool reverseEnumeration)
    {
        var fileSystem = new FakeFileSystem { ReverseEnumeration = reverseEnumeration };
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile("C:\\root\\a.txt", 3, BaseWrite.AddMinutes(1));
        fileSystem.AddFile("C:\\root\\m.txt", 4, BaseWrite.AddMinutes(2));
        fileSystem.AddFile("C:\\root\\z.txt", 5, BaseWrite.AddMinutes(3));
        return fileSystem;
    }

    private static string InspectStandardTree(long size, DateTimeOffset write, string fileName)
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("C:\\root", BaseWrite);
        fileSystem.AddFile($"C:\\root\\{fileName}", size, write);
        var inspection = new CleanupTreeInspector(fileSystem)
            .Inspect("C:\\root", allowMissing: false);
        return inspection.Observation!.ObservationDigest;
    }

    private sealed class FakeFileSystem : ICleanupFileSystem
    {
        private readonly Dictionary<string, Node> _nodes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _children =
            new(StringComparer.Ordinal);

        public bool ReverseEnumeration { get; init; }

        public void AddDirectory(
            string path,
            DateTimeOffset lastWrite,
            bool reparsePoint = false)
        {
            var attributes = FileAttributes.Directory;
            if (reparsePoint)
            {
                attributes |= FileAttributes.ReparsePoint;
            }

            Register(path, new Node(attributes, 0, lastWrite, false));
            _children.TryAdd(path, []);
        }

        public void AddFile(
            string path,
            long length,
            DateTimeOffset lastWrite,
            bool reparsePoint = false,
            bool unreadable = false)
        {
            var attributes = FileAttributes.Normal;
            if (reparsePoint)
            {
                attributes |= FileAttributes.ReparsePoint;
            }

            Register(path, new Node(attributes, length, lastWrite, unreadable));
        }

        public void AddFileAllowingCollision(
            string path,
            long length,
            DateTimeOffset lastWrite)
        {
            // Registers a sibling whose name collides ignoring case (impossible on a
            // real NTFS volume, but the inspector must still fail closed).
            _nodes[path] = new Node(FileAttributes.Normal, length, lastWrite, false);
            var parent = Path.GetDirectoryName(path)!;
            _children[parent].Add(path);
        }

        public FileAttributes GetAttributes(string path)
        {
            if (!_nodes.TryGetValue(path, out var node))
            {
                throw new FileNotFoundException(path);
            }

            if (node.Unreadable)
            {
                throw new IOException($"'{path}' is locked.");
            }

            return node.Attributes;
        }

        public IEnumerable<string> EnumerateEntries(string path)
        {
            var children = _children.TryGetValue(path, out var list)
                ? list.ToList()
                : [];
            if (ReverseEnumeration)
            {
                children.Reverse();
            }

            return children;
        }

        public long GetFileLength(string path) => _nodes[path].Length;

        public DateTimeOffset GetLastWriteUtc(string path) => _nodes[path].LastWriteUtc;

        private void Register(string path, Node node)
        {
            _nodes[path] = node;
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && _children.TryGetValue(parent, out var siblings))
            {
                siblings.Add(path);
            }
        }

        private sealed record Node(
            FileAttributes Attributes,
            long Length,
            DateTimeOffset LastWriteUtc,
            bool Unreadable);
    }
}
